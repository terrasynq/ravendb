﻿//-----------------------------------------------------------------------
// <copyright file="MultiLoaderWithInclude.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Raven.Client.Documents.Indexes;
using Raven.Client.Extensions;

namespace Raven.Client.Documents.Session.Loaders
{
    /// <summary>
    /// Fluent implementation for specifying include paths
    /// for loading documents
    /// </summary>
    internal class MultiLoaderWithInclude<T> : ILoaderWithInclude<T>
    {
        private readonly IDocumentSessionImpl _session;
        private readonly List<string> _includes = new List<string>();

        /// <summary>
        /// Includes the specified path.
        /// </summary>
        /// <param name="path">The path.</param>
        public ILoaderWithInclude<T> Include(string path)
        {
            return Include(path, typeof(object));
        }

        ILoaderWithInclude<T> Include(string path, Type type)
        {
            _includes.Add(path);
            return this;
        }

        /// <summary>
        /// Includes the specified path.
        /// </summary>
        /// <param name="path">The path.</param>
        public ILoaderWithInclude<T> Include(Expression<Func<T, string>> path)
        {
            return Include(path.ToPropertyPath());
        }

        /// <summary>
        /// Includes the specified path.
        /// </summary>
        /// <param name="path">The path.</param>
        public ILoaderWithInclude<T> Include<TInclude>(Expression<Func<T, string>> path)
        {
            return Include(path.ToPropertyPath(), typeof(TInclude));
        }

        /// <summary>
        /// Includes the specified path.
        /// </summary>
        /// <param name="path">The path.</param>
        public ILoaderWithInclude<T> Include(Expression<Func<T, IEnumerable<string>>> path)
        {
            return Include(path.ToPropertyPath());
        }

        /// <summary>
        /// Includes the specified path.
        /// </summary>
        /// <param name="path">The path.</param>
        public ILoaderWithInclude<T> Include<TInclude>(Expression<Func<T, IEnumerable<string>>> path)
        {
            return Include(path.ToPropertyPath(), typeof(TInclude));
        }

        /// <summary>
        /// Loads the specified ids.
        /// </summary>
        /// <param name="ids">The ids.</param>
        /// <returns></returns>
        public Dictionary<string, T> Load(params string[] ids)
        {
            return _session.LoadInternal<T>(ids, _includes.ToArray());
        }

        /// <summary>
        /// Loads the specified ids.
        /// </summary>
        /// <param name="ids">The ids.</param>
        /// <returns></returns>
        public Dictionary<string, T> Load(IEnumerable<string> ids)
        {
            return _session.LoadInternal<T>(ids.ToArray(), _includes.ToArray());
        }

        /// <summary>
        /// Loads the specified id.
        /// </summary>
        /// <param name="id">The id.</param>
        public T Load(string id)
        {
            return _session.LoadInternal<T>(new[] { id }, _includes.ToArray()).Values.FirstOrDefault();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiLoaderWithInclude{T}"/> class.
        /// </summary>
        /// <param name="session">The session.</param>
        internal MultiLoaderWithInclude(IDocumentSessionImpl session)
        {
            _session = session;
        }

        /// <summary>
        /// Loads the specified ids.
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="ids">The ids.</param>
        public Dictionary<string, TResult> Load<TResult>(params string[] ids)
        {
            return _session.LoadInternal<TResult>(ids, _includes.ToArray());
        }

        /// <summary>
        /// Loads the specified ids.
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="ids">The ids.</param>
        public Dictionary<string, TResult> Load<TResult>(IEnumerable<string> ids)
        {
            return _session.LoadInternal<TResult>(ids.ToArray(), _includes.ToArray());
        }

        /// <summary>
        /// Loads the specified id.
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="id">The id.</param>
        public TResult Load<TResult>(string id)
        {
            return Load<TResult>(new[] { id }).Values.FirstOrDefault();
        }

        public TResult Load<TTransformer, TResult>(string id, Action<ILoadConfiguration> configure = null)
            where TTransformer : AbstractTransformerCreationTask, new()
        {
            var transformer = new TTransformer().TransformerName;
            var configuration = new LoadConfiguration();
            configure?.Invoke(configuration);

            var result = _session.LoadInternal<TResult>(new[] { id }, _includes.ToArray(), transformer, configuration.TransformerParameters);
            if (result.Count == 0)
                return default(TResult);

            Debug.Assert(result.Count == 1);

            return result[id];
        }

        public Dictionary<string, TResult> Load<TTransformer, TResult>(IEnumerable<string> ids, Action<ILoadConfiguration> configure = null)
            where TTransformer : AbstractTransformerCreationTask, new()
        {
            var transformer = new TTransformer().TransformerName;
            var configuration = new LoadConfiguration();
            configure?.Invoke(configuration);

            return _session.LoadInternal<TResult>(ids.ToArray(), _includes.ToArray(), transformer, configuration.TransformerParameters);
        }
    }
}
