﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

using Lucene.Net.Analysis;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;

using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Indexing;
using Raven.Abstractions.Logging;
using Raven.Server.Config.Categories;
using Raven.Server.Documents.Indexes.Persistance.Lucene.Documents;
using Raven.Server.Documents.Queries;
using Raven.Server.Indexing;
using Voron;
using Voron.Impl;
using Constants = Raven.Abstractions.Data.Constants;
using Directory = Lucene.Net.Store.Directory;
using Version = Lucene.Net.Util.Version;

namespace Raven.Server.Documents.Indexes.Persistance.Lucene
{
    public class LuceneIndexPersistence : IDisposable
    {
        private readonly Analyzer _dummyAnalyzer = new SimpleAnalyzer();

        private readonly int _indexId;

        private readonly IndexDefinitionBase _definition;
        
        private readonly LuceneDocumentConverter _converter;

        private static readonly StopAnalyzer StopAnalyzer = new StopAnalyzer(Version.LUCENE_30);

        private readonly object _writeLock = new object();

        private LuceneIndexWriter _indexWriter;

        private SnapshotDeletionPolicy _snapshotter;

        private LuceneVoronDirectory _directory;

        private readonly IndexSearcherHolder _indexSearcherHolder = new IndexSearcherHolder();

        private bool _disposed;

        private bool _initialized;

        public LuceneIndexPersistence(int indexId, IndexDefinitionBase indexDefinition)
        {
            _indexId = indexId;
            _definition = indexDefinition;
            _converter = new LuceneDocumentConverter(_definition.MapFields);
        }

        public void Initialize(StorageEnvironment environment, IndexingConfiguration configuration)
        {
            if (_initialized)
                throw new InvalidOperationException();

            lock (_writeLock)
            {
                if (_initialized)
                    throw new InvalidOperationException();

                _directory = new LuceneVoronDirectory(environment);

                using (var tx = environment.WriteTransaction())
                {
                    using (_directory.SetTransaction(tx))
                    {
                        CreateIndexStructure();
                        RecreateSearcher();
                    }

                    tx.Commit();
                }


                _initialized = true;
            }
        }

        private void CreateIndexStructure()
        {
            new IndexWriter(_directory, _dummyAnalyzer, IndexWriter.MaxFieldLength.UNLIMITED).Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Index));

            lock (_writeLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(Index));

                _disposed = true;

                _indexWriter?.Analyzer?.Dispose();
                _indexWriter?.Dispose();
                _converter?.Dispose();
                _directory?.Dispose();
            }
        }

        public IIndexWriteActions OpenIndexWriter(Transaction writeTransaction)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index persistance for index '{_definition.Name} ({_indexId})' was already disposed.");

            if (_initialized == false)
                throw new InvalidOperationException($"Index persistance for index '{_definition.Name} ({_indexId})' was not initialized.");

            return new LuceneIndexWriteActions(this, writeTransaction);
        }

        public IIndexReadActions OpenIndexReader(Transaction readTransaction)
        {
            if (_disposed)
                throw new ObjectDisposedException($"Index persistance for index '{_definition.Name} ({_indexId})' was already disposed.");

            if (_initialized == false)
                throw new InvalidOperationException($"Index persistance for index '{_definition.Name} ({_indexId})' was not initialized.");

            return new LuceneIndexReadActions(this, readTransaction);
        }

        private void Flush()
        {
            try
            {
                lock (_writeLock)
                {
                    if (_disposed)
                        return;
                    if (_indexWriter == null)
                        return;

                    _indexWriter.Commit();
                }
            }
            catch (Exception)
            {
                throw;
            }
        }

        public IDisposable GetSearcher(out IndexSearcher searcher)
        {
            return _indexSearcherHolder.GetSearcher(out searcher);
        }

        private void RecreateSearcher()
        {
            if (_indexWriter == null)
            {
                _indexSearcherHolder.SetIndexSearcher(new IndexSearcher(_directory, true), wait: false);
            }
            else
            {
                var indexReader = _indexWriter.GetReader();
                _indexSearcherHolder.SetIndexSearcher(new IndexSearcher(indexReader), wait: false);
            }
        }

        private void CreateIndexWriter()
        {
            try
            {
                _snapshotter = new SnapshotDeletionPolicy(new KeepOnlyLastCommitDeletionPolicy());
                _indexWriter = new LuceneIndexWriter(_directory, StopAnalyzer, _snapshotter, IndexWriter.MaxFieldLength.UNLIMITED, 1024, null);
            }
            catch (Exception)
            {
                throw;
            }
        }

        private void EnsureIndexWriter()
        {
            try
            {
                if (_indexWriter == null)
                    CreateIndexWriter();
            }
            catch (IOException)
            {
                throw;
            }
        }

        private class LuceneIndexWriteActions : IIndexWriteActions
        {
            private readonly Term _documentId = new Term(Constants.DocumentIdFieldName, "Dummy");

            private readonly LuceneIndexPersistence _persistence;

            private readonly LowerCaseKeywordAnalyzer _analyzer;

            private readonly Lock _locker;

            private readonly IDisposable _releaseWriteTransaction;

            public LuceneIndexWriteActions(LuceneIndexPersistence persistence, Transaction writeTransaction)
            {
                _persistence = persistence;

                Monitor.Enter(_persistence._writeLock);

                _analyzer = new LowerCaseKeywordAnalyzer();

                _releaseWriteTransaction = persistence._directory.SetTransaction(writeTransaction);

                _persistence.EnsureIndexWriter();

                _locker = _persistence._directory.MakeLock("writing-to-index.lock");

                if (_locker.Obtain() == false)
                    throw new InvalidOperationException();
            }

            public void Dispose()
            {
                try
                {
                    if (_persistence._indexWriter != null) // TODO && _persistance._indexWriter.RamSizeInBytes() >= long.MaxValue)
                        _persistence.Flush(); // just make sure changes are flushed to disk

                    _persistence.RecreateSearcher();

                    _releaseWriteTransaction?.Dispose();
                }
                finally
                {
                    _locker?.Release();
                    _analyzer?.Dispose();

                    Monitor.Exit(_persistence._writeLock);
                }
            }

            public void IndexDocument(Document document)
            {
                var luceneDoc = _persistence._converter.ConvertToCachedDocument(document);

                _persistence._indexWriter.AddDocument(luceneDoc, _analyzer);
            }

            public void Delete(string key)
            {
                _persistence._indexWriter.DeleteDocuments(_documentId.CreateTerm(key));
            }
        }

        private class LuceneIndexReadActions : IIndexReadActions
        {
            private const string _Range = "_Range";

            private static readonly ILog Log = LogManager.GetLogger(typeof(LuceneIndexReadActions).FullName);
            private static readonly CompareInfo InvariantCompare = CultureInfo.InvariantCulture.CompareInfo;

            private readonly string _indexName;
            private readonly IndexSearcher _searcher;
            private readonly LowerCaseKeywordAnalyzer _analyzer;
            private readonly IDisposable _releaseSearcher;
            private LuceneVoronDirectory _directory;
            private readonly IDisposable _releaseReadTransaction;

            public LuceneIndexReadActions(LuceneIndexPersistence persistence, Transaction readTransaction)
            {
                _analyzer = new LowerCaseKeywordAnalyzer();
                _indexName = persistence._definition.Name;
                _directory = persistence._directory;
                _releaseReadTransaction = _directory.SetTransaction(readTransaction);
                _releaseSearcher = persistence.GetSearcher(out _searcher);
            }

            public IEnumerable<string> Query(IndexQuery query, CancellationToken token, Reference<int> totalResults)
            {
                var docsToGet = query.PageSize;
                var position = query.Start;

                var luceneQuery = GetLuceneQuery(query);
                var returnedResults = 0;
                var endOfResults = false;

                do
                {
                    token.ThrowIfCancellationRequested();

                    var search = ExecuteQuery(luceneQuery, query.Start, docsToGet, query.SortedFields);

                    totalResults.Value = search.TotalHits;

                    //RecordAlreadyPagedItemsInPreviousPage(start, search, indexSearcher);

                    //SetupHighlighter(documentQuery);

                    for (; position < search.ScoreDocs.Length && query.PageSize > 0; position++)
                    {
                        token.ThrowIfCancellationRequested();

                        var scoreDoc = search.ScoreDocs[position];
                        var document = _searcher.Doc(scoreDoc.Doc);

                        //var indexQueryResult = parent.RetrieveDocument(document, fieldsToFetch, scoreDoc);
                        //if (indexQueryResult.Key == null && !string.IsNullOrEmpty(indexQuery.HighlighterKeyName))
                        //{
                        //    indexQueryResult.HighlighterKey = document.Get(indexQuery.HighlighterKeyName);
                        //}

                        //if (ShouldIncludeInResults(indexQueryResult) == false)
                        //{
                        //    indexQuery.SkippedResults.Value++;
                        //    continue;
                        //}

                        //AddHighlighterResults(indexSearcher, scoreDoc, indexQueryResult);

                        //AddQueryExplanation(documentQuery, indexSearcher, scoreDoc, indexQueryResult);

                        returnedResults++;

                        yield return document.Get(Constants.DocumentIdFieldName);
                        if (returnedResults == query.PageSize)
                            yield break;
                    }

                    //if (hasMultipleIndexOutputs)
                    //    docsToGet += (pageSize - returnedResults) * maxNumberOfIndexOutputs;
                    //else
                        docsToGet += (query.PageSize - returnedResults);

                    endOfResults = search.TotalHits == search.ScoreDocs.Length;

                } while (returnedResults < query.PageSize && endOfResults == false);
            } 

            private TopDocs ExecuteQuery(Query documentQuery, int start, int pageSize, SortedField[] sortedFields)
            {
                // TODO arek
                //if (pageSize == int.MaxValue && sortedFields == null) // we want all docs, no sorting required
                //{
                //    var gatherAllCollector = new GatherAllCollector();
                //    indexSearcher.Search(documentQuery, gatherAllCollector);
                //    return gatherAllCollector.ToTopDocs();
                //}

                var absFullPage = Math.Abs(pageSize + start); // need to protect against ridiculously high values of pageSize + start that overflow
                var minPageSize = Math.Max(absFullPage, 1);

                // NOTE: We get Start + Pagesize results back so we have something to page on
                if (sortedFields != null)
                {
                    var sort = GetSort(sortedFields);

                    _searcher.SetDefaultFieldSortScoring(true, false);
                    try
                    {
                        return _searcher.Search(documentQuery, null, minPageSize, sort); ;
                    }
                    finally
                    {
                        _searcher.SetDefaultFieldSortScoring(false, false);
                    }
                }

                return _searcher.Search(documentQuery, null, minPageSize);
            }

            private Query GetLuceneQuery(IndexQuery query)
            {
                Query documentQuery;

                if (string.IsNullOrEmpty(query.Query))
                {
                    if (Log.IsDebugEnabled)
                        Log.Debug($"Issuing query on index {_indexName} for all documents");

                    documentQuery = new MatchAllDocsQuery();
                }
                else
                {
                    if (Log.IsDebugEnabled)
                        Log.Debug($"Issuing query on index {_indexName} for: {query.Query}");

                    var toDispose = new List<Action>();
                   // RavenPerFieldAnalyzerWrapper searchAnalyzer = null;
                    try
                    {
                        //_persistance._a
                        //searchAnalyzer = parent.CreateAnalyzer(new LowerCaseKeywordAnalyzer(), toDispose, true);
                        //searchAnalyzer = parent.AnalyzerGenerators.Aggregate(searchAnalyzer, (currentAnalyzer, generator) =>
                        //{
                        //    Analyzer newAnalyzer = generator.GenerateAnalyzerForQuerying(parent.PublicName, query.Query, currentAnalyzer);
                        //    if (newAnalyzer != currentAnalyzer)
                        //    {
                        //        DisposeAnalyzerAndFriends(toDispose, currentAnalyzer);
                        //    }
                        //    return parent.CreateAnalyzer(newAnalyzer, toDispose, true);
                        //});

                        documentQuery = QueryBuilder.BuildQuery(query.Query, query, _analyzer);
                    }
                    finally
                    {
                        //DisposeAnalyzerAndFriends(toDispose, searchAnalyzer);
                    }
                }

                //var afterTriggers = ApplyIndexTriggers(documentQuery);
               
                return documentQuery;
            }

            private Sort GetSort(SortedField[] sortedFields)
            {
                return new Sort(sortedFields.Select(x =>
                {
                    var sortOptions = SortOptions.String;

                    if (InvariantCompare.IsSuffix(x.Field, _Range, CompareOptions.None))
                    {
                        sortOptions = SortOptions.Double;
                    }

                    return new SortField(x.Field, (int) sortOptions, x.Descending);
                }).ToArray());
            }

            public void Dispose()
            {
                _analyzer?.Dispose();
                _releaseSearcher?.Dispose();
               _releaseReadTransaction?.Dispose();
            }
        }
    }
}