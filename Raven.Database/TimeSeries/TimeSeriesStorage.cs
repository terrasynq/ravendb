﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Raven.Abstractions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.TimeSeries;
using Raven.Abstractions.Util;
using Raven.Database.Config;
using Raven.Database.Extensions;
using Raven.Database.Impl;
using Raven.Database.Server.Abstractions;
using Raven.Database.Server.Connections;
using Raven.Database.TimeSeries.Notifications;
using Raven.Database.Util;
using Voron;
using Voron.Impl;
using Voron.Trees;
using Voron.Trees.Fixed;
using Voron.Util;
using Voron.Util.Conversion;
using Constants = Raven.Abstractions.Data.Constants;

namespace Raven.Database.TimeSeries
{
	public class TimeSeriesStorage : IResourceStore, IDisposable
	{
		private static readonly ILog Log = LogManager.GetCurrentClassLogger();

		private volatile bool disposed;

		private readonly StorageEnvironment storageEnvironment;
		private readonly TransportState transportState;
		private readonly NotificationPublisher notificationPublisher;
		private readonly TimeSeriesMetricsManager metricsTimeSeries;
		private Tree metadata;

		public Guid ServerId { get; set; }

		public string TimeSeriesUrl { get; private set; }

		public string Name { get; private set; }
		public string ResourceName { get; private set; }
		public TransportState TransportState { get; private set; }
		public AtomicDictionary<object> ExtensionsState { get; private set; }
		public InMemoryRavenConfiguration Configuration { get; private set; }
		public DateTime LastWrite { get; set; }

		[CLSCompliant(false)]
		public TimeSeriesMetricsManager MetricsTimeSeries
		{
			get { return metricsTimeSeries; }
		}

		public TimeSeriesStorage(string serverUrl, string timeSeriesName, InMemoryRavenConfiguration configuration, TransportState receivedTransportState = null)
		{
			Name = timeSeriesName;
			TimeSeriesUrl = string.Format("{0}ts/{1}", serverUrl, timeSeriesName);
			ResourceName = string.Concat(Constants.TimeSeries.UrlPrefix, "/", timeSeriesName);

			metricsTimeSeries = new TimeSeriesMetricsManager();

			var options = configuration.RunInMemory ? StorageEnvironmentOptions.CreateMemoryOnly()
				: CreateStorageOptionsFromConfiguration(configuration.TimeSeries.DataDirectory, configuration.Settings);

			storageEnvironment = new StorageEnvironment(options);
			transportState = receivedTransportState ?? new TransportState();
			notificationPublisher = new NotificationPublisher(transportState);
			ExtensionsState = new AtomicDictionary<object>();

			Configuration = configuration;
			Initialize();

			AppDomain.CurrentDomain.ProcessExit += ShouldDispose;
			AppDomain.CurrentDomain.DomainUnload += ShouldDispose;
		}

		private void Initialize()
		{
			using (var tx = storageEnvironment.NewTransaction(TransactionFlags.ReadWrite))
			{
				storageEnvironment.CreateTree(tx, "data", keysPrefixing: true);

				metadata = storageEnvironment.CreateTree(tx, "$metadata");
				var id = metadata.Read("id");
				if (id == null) // new db
				{
					ServerId = Guid.NewGuid();
					metadata.Add("id", ServerId.ToByteArray());
				}
				else
				{
					int used;
					ServerId = new Guid(id.Reader.ReadBytes(16, out used));
				}

				tx.Commit();
			}
		}

		private static StorageEnvironmentOptions CreateStorageOptionsFromConfiguration(string path, NameValueCollection settings)
		{
			bool result;
			if (bool.TryParse(settings[Constants.RunInMemory] ?? "false", out result) && result)
				return StorageEnvironmentOptions.CreateMemoryOnly();

			bool allowIncrementalBackupsSetting;
			if (bool.TryParse(settings[Constants.Voron.AllowIncrementalBackups] ?? "false", out allowIncrementalBackupsSetting) == false)
				throw new ArgumentException(Constants.Voron.AllowIncrementalBackups + " settings key contains invalid value");

			var directoryPath = path ?? AppDomain.CurrentDomain.BaseDirectory;
			var filePathFolder = new DirectoryInfo(directoryPath);
			if (filePathFolder.Exists == false)
				filePathFolder.Create();

			var tempPath = settings[Constants.Voron.TempPath];
			var journalPath = settings[Constants.RavenTxJournalPath];
			var options = StorageEnvironmentOptions.ForPath(directoryPath, tempPath, journalPath);
			options.IncrementalBackupEnabled = allowIncrementalBackupsSetting;
			return options;
		}

		public Reader CreateReader(byte seriesValueLength)
		{
			if (seriesValueLength < 1)
				throw new ArgumentOutOfRangeException("seriesValueLength", "Should be equal or greater than 1");
		
			return new Reader(this, seriesValueLength);
		}

		public Writer CreateWriter(byte seriesValueLength)
		{
			if (seriesValueLength < 1)
				throw new ArgumentOutOfRangeException("seriesValueLength", "Should be equal or greater than 1");
			
			LastWrite = SystemTime.UtcNow;
			return new Writer(this, seriesValueLength);
		}

		public class Point
		{
#if DEBUG
			public string DebugKey { get; set; }
#endif
			public DateTime At { get; set; }

			public double[] Values { get; set; }
			
			public double Value
			{
				get { return Values[0]; }
			}
		}

		internal const string SeriesTreePrefix = "series-";
		internal const string PeriodTreePrefix = "periods-";
		internal const char PeriodsKeySeparator = '\uF8FF';
		internal const string PrefixesPrefix = "prefixes-";

		public class Reader : IDisposable
		{
			private readonly TimeSeriesStorage storage;
			private readonly byte seriesValueLength;
			private readonly Transaction tx;
			private readonly Tree tree;

			public Reader(TimeSeriesStorage storage, byte seriesValueLength)
			{
				this.storage = storage;
				this.seriesValueLength = seriesValueLength;
				tx = this.storage.storageEnvironment.NewTransaction(TransactionFlags.Read);
				tree = tx.ReadTree(SeriesTreePrefix + seriesValueLength);
			}

			public IEnumerable<Point> Query(TimeSeriesQuery query)
			{
				return GetRawQueryResult(query);
			}

			public IEnumerable<Point>[] Query(params TimeSeriesQuery[] queries)
			{
				var result = new IEnumerable<Point>[queries.Length];
				for (int i = 0; i < queries.Length; i++)
				{
					result[i] = GetRawQueryResult(queries[i]);
				}
				return result;
			}

			public IEnumerable<Range> QueryRollup(TimeSeriesRollupQuery query)
			{
				return GetQueryRollup(query);
			}

			public IEnumerable<Range>[] QueryRollup(params TimeSeriesRollupQuery[] queries)
			{
				var result = new IEnumerable<Range>[queries.Length];
				for (int i = 0; i < queries.Length; i++)
				{
					result[i] = GetQueryRollup(queries[i]);
				}
				return result;
			}

			private IEnumerable<Range> GetQueryRollup(TimeSeriesRollupQuery query)
			{
				switch (query.Duration.Type)
				{
					case PeriodType.Seconds:
						if (query.Start.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by seconds, you cannot specify milliseconds");
						if (query.End.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by seconds, you cannot specify milliseconds");
						if (query.Start.Second%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that starts from midnight", query.Duration.Duration, query.Duration.Type));
						if (query.End.Second%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that ends in midnight", query.Duration.Duration, query.Duration.Type));
						break;
					case PeriodType.Minutes:
						if (query.Start.Second != 0 || query.Start.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by minutes, you cannot specify seconds or milliseconds");
						if (query.End.Second != 0 || query.End.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by minutes, you cannot specify seconds or milliseconds");
						if (query.Start.Minute%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that starts from midnight", query.Duration.Duration, query.Duration.Type));
						if (query.End.Minute%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that ends in midnight", query.Duration.Duration, query.Duration.Type));
						break;
					case PeriodType.Hours:
						if (query.Start.Minute != 0 || query.Start.Second != 0 || query.Start.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by hours, you cannot specify minutes, seconds or milliseconds");
						if (query.End.Minute != 0 || query.End.Second != 0 || query.End.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by hours, you cannot specify minutes, seconds or milliseconds");
						if (query.Start.Hour%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that starts from midnight", query.Duration.Duration, query.Duration.Type));
						if (query.End.Hour%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that ends in midnight", query.Duration.Duration, query.Duration.Type));
						break;
					case PeriodType.Days:
						if (query.Start.Hour != 0 || query.Start.Minute != 0 || query.Start.Second != 0 || query.Start.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by hours, you cannot specify hours, minutes, seconds or milliseconds");
						if (query.End.Hour != 0 || query.End.Minute != 0 || query.End.Second != 0 || query.End.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by hours, you cannot specify hours, minutes, seconds or milliseconds");
						if (query.Start.Day%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that starts from midnight", query.Duration.Duration, query.Duration.Type));
						if (query.End.Day%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that ends in midnight", query.Duration.Duration, query.Duration.Type));
						break;
					case PeriodType.Months:
						if (query.Start.Day != 1 || query.Start.Hour != 0 || query.Start.Minute != 0 || query.Start.Second != 0 || query.Start.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by hours, you cannot specify days, hours, minutes, seconds or milliseconds");
						if (query.End.Day != 1 || query.End.Minute != 0 || query.End.Second != 0 || query.End.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by hours, you cannot specify days, hours, minutes, seconds or milliseconds");
						if (query.Start.Month%(query.Duration.Duration) != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that starts from midnight", query.Duration.Duration, query.Duration.Type));
						if (query.End.Month%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that ends in midnight", query.Duration.Duration, query.Duration.Type));
						break;
					case PeriodType.Years:
						if (query.Start.Month != 1 || query.Start.Day != 1 || query.Start.Hour != 0 || query.Start.Minute != 0 || query.Start.Second != 0 || query.Start.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by years, you cannot specify months, days, hours, minutes, seconds or milliseconds");
						if (query.End.Month != 1 || query.End.Day != 1 || query.End.Minute != 0 || query.End.Second != 0 || query.End.Millisecond != 0)
							throw new InvalidOperationException("When querying a roll up by years, you cannot specify months, days, hours, minutes, seconds or milliseconds");
						if (query.Start.Year%(query.Duration.Duration) != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that starts from midnight", query.Duration.Duration, query.Duration.Type));
						if (query.End.Year%query.Duration.Duration != 0)
							throw new InvalidOperationException(string.Format("Cannot create a roll up by {0} {1} as it cannot be divided to candles that ends in midnight", query.Duration.Duration, query.Duration.Type));
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}

				if (tree == null)
					yield break;

				using (var periodTx = storage.storageEnvironment.NewTransaction(TransactionFlags.ReadWrite))
				{
					var fixedTree = tree.FixedTreeFor(query.Key, (byte)(seriesValueLength * sizeof(double)));
					var periodFixedTree = periodTx.State.GetTree(periodTx, PeriodTreePrefix + seriesValueLength)
						.FixedTreeFor(query.Key + PeriodsKeySeparator + query.Duration.Type + "-" + query.Duration.Duration, (byte)(seriesValueLength * Range.RangeValue.StorageItemsLength * sizeof(double)));

					using (var periodWriter = new RollupWriter(periodFixedTree, seriesValueLength))
					{
						using (var periodTreeIterator = periodFixedTree.Iterate())
						using (var rawTreeIterator = fixedTree.Iterate())
						{
							foreach (var range in GetRanges(query))
							{
								// seek period tree iterator, if found exact match!!, add and move to the next
								if (periodTreeIterator.Seek(range.StartAt.Ticks))
								{
									if (periodTreeIterator.CurrentKey == range.StartAt.Ticks)
									{
										var valueReader = periodTreeIterator.CreateReaderForCurrent();
										int used;
										var bytes = valueReader.ReadBytes(seriesValueLength * Range.RangeValue.StorageItemsLength * sizeof(double), out used);
										Debug.Assert(used == seriesValueLength*Range.RangeValue.StorageItemsLength*sizeof (double));

										for (int i = 0; i < seriesValueLength; i++)
										{
											var startPosition = i * Range.RangeValue.StorageItemsLength;
											range.Values[i].Volume = EndianBitConverter.Big.ToDouble(bytes, (startPosition + 0) * sizeof(double));
											if (range.Values[i].Volume != 0)
											{
												range.Values[i].High = EndianBitConverter.Big.ToDouble(bytes, (startPosition + 1)*sizeof (double));
												range.Values[i].Low = EndianBitConverter.Big.ToDouble(bytes, (startPosition + 2)*sizeof (double));
												range.Values[i].Open = EndianBitConverter.Big.ToDouble(bytes, (startPosition + 3)*sizeof (double));
												range.Values[i].Close = EndianBitConverter.Big.ToDouble(bytes, (startPosition + 4)*sizeof (double));
												range.Values[i].Sum = EndianBitConverter.Big.ToDouble(bytes, (startPosition + 5)*sizeof (double));
											}
										}
										yield return range;
										continue;
									}
								}

								// seek tree iterator, if found note don't go to next range!!, sum, add to period tree, move next
								if (range.StartAt.Minute == 0 && range.StartAt.Second == 0)
								{
									
								}
								if (rawTreeIterator.Seek(range.StartAt.Ticks))
								{
									GetAllPointsForRange(rawTreeIterator, range);
								}

								// if not found, create empty periods until the end or the next valid period
								/*if (range.Volume == 0)
								{
								}*/

								periodWriter.Append(range.StartAt, range);
								yield return range;
							}
						}
					}
					periodTx.Commit();
				}
			}

			private void GetAllPointsForRange(FixedSizeTree.IFixedSizeIterator rawTreeIterator, Range range)
			{
				var endTicks = range.Duration.AddToDateTime(range.StartAt).Ticks;
				var buffer = new byte[sizeof(double) * seriesValueLength];
				var firstPoint = true;

				do
				{
					var ticks = rawTreeIterator.CurrentKey;
					if (ticks >= endTicks)
						return;

					var point = new Point
					{
#if DEBUG
						DebugKey = range.DebugKey,
#endif
						At = new DateTime(ticks),
						Values = new double[seriesValueLength],
					};

					var reader = rawTreeIterator.CreateReaderForCurrent();
					reader.Read(buffer, 0, sizeof(double) * seriesValueLength);

					for (int i = 0; i < seriesValueLength; i++)
					{
						var value = point.Values[i] = EndianBitConverter.Big.ToDouble(buffer, i * sizeof(double));

						if (firstPoint)
						{
							range.Values[i].Open = range.Values[i].High = range.Values[i].Low = range.Values[i].Sum = value;
							range.Values[i].Volume = 1;
						}
						else
						{
							range.Values[i].High = Math.Max(range.Values[i].High, value);
							range.Values[i].Low = Math.Min(range.Values[i].Low, value);
							range.Values[i].Sum += value;
							range.Values[i].Volume += 1;
						}

						range.Values[i].Close = value;

					}
					firstPoint = false;

				} while (rawTreeIterator.MoveNext());
			}

			private IEnumerable<Range> GetRanges(TimeSeriesRollupQuery query)
			{
				var startAt = query.Start;
				while (true)
				{
					var nextStartAt = query.Duration.AddToDateTime(startAt);
					if (startAt == query.End)
						yield break;
					if (nextStartAt > query.End)
					{
						throw new InvalidOperationException("Debug: Duration is not aligned with the end of the range.");
					}
					var rangeValues = new Range.RangeValue[seriesValueLength];
					for (int i = 0; i < seriesValueLength; i++)
					{
						rangeValues[i] = new Range.RangeValue();
					}
					yield return new Range
					{
#if DEBUG
						DebugKey = query.Key,
#endif
						StartAt = startAt,
						Duration = query.Duration,
						Values = rangeValues,
					};
					startAt = nextStartAt;
				}
			}

			private IEnumerable<Point> GetRawQueryResult(TimeSeriesQuery query)
			{
				if (tree == null)
					return Enumerable.Empty<Point>();

				var buffer = new byte[seriesValueLength * sizeof(double)];

				var fixedTree = tree.FixedTreeFor(query.Key, (byte) (seriesValueLength*sizeof (double)));
				return IterateOnTree(query, fixedTree, it =>
				{
					var point = new Point
					{
#if DEBUG
						DebugKey = fixedTree.Name.ToString(),
#endif
						At = new DateTime(it.CurrentKey),
						Values = new double[seriesValueLength],
					};
					
					var reader = it.CreateReaderForCurrent();
					reader.Read(buffer, 0, seriesValueLength * sizeof (double));
					for (int i = 0; i < seriesValueLength; i++)
					{
						point.Values[i] = EndianBitConverter.Big.ToDouble(buffer, i * sizeof(double));
					}
					return point;
				});
			}

			public static IEnumerable<T> IterateOnTree<T>(TimeSeriesQuery query, FixedSizeTree fixedTree, Func<FixedSizeTree.IFixedSizeIterator, T> iteratorFunc)
			{
				using (var it = fixedTree.Iterate())
				{
					if (it.Seek(query.Start.Ticks) == false)
						yield break;

					do
					{
						if (it.CurrentKey > query.End.Ticks)
							yield break;

						yield return iteratorFunc(it);
					} while (it.MoveNext());
				}
			}

			public void Dispose()
			{
				if (tx != null)
					tx.Dispose();
			}

			public long GetTimeSeriesCount()
			{
				throw new InvalidOperationException();
			}
		}

		public class Writer : IDisposable
		{
			private readonly byte seriesValueLength;
			private readonly Transaction tx;

			private readonly Tree tree;
			private readonly Dictionary<string, RollupRange> rollupsToClear = new Dictionary<string, RollupRange>();
			private byte[] valBuffer;

			public Writer(TimeSeriesStorage storage, byte seriesValueLength)
			{
				this.seriesValueLength = seriesValueLength;
				tx = storage.storageEnvironment.NewTransaction(TransactionFlags.ReadWrite);
				tree = tx.State.GetTree(tx, SeriesTreePrefix + seriesValueLength);
			}

			public void Append(string key, DateTime time, params double[] values)
			{
				if (values.Length != seriesValueLength)
					throw new ArgumentOutOfRangeException("values", string.Format("Appended values should be the same length the series values length which is {0} and not {1}", seriesValueLength, values.Length));

				RollupRange range;
				if (rollupsToClear.TryGetValue(key, out range))
				{
					if (time > range.End)
					{
						range.End = time;
					}
					else if (time < range.Start)
					{
						range.Start = time;
					}
				}
				else
				{
					rollupsToClear.Add(key, new RollupRange(time));
				}

				if (valBuffer == null)
					valBuffer = new byte[seriesValueLength*sizeof (double)];
				for (int i = 0; i < values.Length; i++)
				{
					EndianBitConverter.Big.CopyBytes(values[i], valBuffer, i * sizeof(double));
				}

				var fixedTree = tree.FixedTreeFor(key, (byte) (seriesValueLength*sizeof (double)));
				fixedTree.Add(time.Ticks, valBuffer);
			}

			public void Dispose()
			{
				if (tx != null)
					tx.Dispose();
			}

			public void Commit()
			{
				CleanRollupDataForRange();
				tx.Commit();
			}

			private void CleanRollupDataForRange()
			{
				var periodTree = tx.ReadTree(PeriodTreePrefix + seriesValueLength);
				if (periodTree == null)
					return;

				foreach (var rollupRange in rollupsToClear)
				{
					using (var it = periodTree.Iterate())
					{
						it.RequiredPrefix = rollupRange.Key + PeriodsKeySeparator;
						if (it.Seek(it.RequiredPrefix) == false)
							continue;

						do
						{
							var periodTreeName = it.CurrentKey.ToString();
							var periodFixedTree = periodTree.FixedTreeFor(periodTreeName, (byte) (seriesValueLength*Range.RangeValue.StorageItemsLength*sizeof (double)));
							if (periodFixedTree == null)
								continue;

							var duration = GetDurationFromTreeName(periodTreeName);
							var keysToDelete = Reader.IterateOnTree(new TimeSeriesQuery
							{
								Key = rollupRange.Key,
								Start = duration.GetStartOfRangeForDateTime(rollupRange.Value.Start),
								End = duration.GetStartOfRangeForDateTime(rollupRange.Value.End),
							}, periodFixedTree, fixedIterator => fixedIterator.CurrentKey).ToArray();

							foreach (var key in keysToDelete)
							{
								periodFixedTree.Delete(key);
							}
						} while (it.MoveNext());
					}
				}
			}

			private PeriodDuration GetDurationFromTreeName(string periodTreeName)
			{
				var separatorIndex = periodTreeName.LastIndexOf(PeriodsKeySeparator);
				var s = periodTreeName.Substring(separatorIndex + 1);
				var strings = s.Split('-');
				return new PeriodDuration(GenericUtil.ParseEnum<PeriodType>(strings[0]), int.Parse(strings[1]));
			}

			public void Delete(string key)
			{
				throw new NotImplementedException();
			}

			public void DeleteRange(string key, long start, long end)
			{
				throw new NotImplementedException();
			}
		}

		public class RollupWriter : IDisposable
		{
			private readonly FixedSizeTree tree;
			private readonly byte seriesValueLength;
			private readonly byte[] valBuffer;

			public RollupWriter(FixedSizeTree tree, byte seriesValueLength)
			{
				this.tree = tree;
				this.seriesValueLength = seriesValueLength;
				valBuffer = new byte[seriesValueLength * Range.RangeValue.StorageItemsLength * sizeof(double)];
			}

			public void Append(DateTime time, Range range)
			{
				for (int i = 0; i < seriesValueLength; i++)
				{
					var rangeValue = range.Values[i];
					var startPosition = i * Range.RangeValue.StorageItemsLength;
					EndianBitConverter.Big.CopyBytes(rangeValue.Volume, valBuffer, (startPosition + 0) * sizeof(double));
					// if (rangeValue.Volume != 0)
					{
						EndianBitConverter.Big.CopyBytes(rangeValue.High, valBuffer, (startPosition + 1) * sizeof(double));
						EndianBitConverter.Big.CopyBytes(rangeValue.Low, valBuffer, (startPosition + 2) * sizeof(double));
						EndianBitConverter.Big.CopyBytes(rangeValue.Open, valBuffer, (startPosition + 3) * sizeof(double));
						EndianBitConverter.Big.CopyBytes(rangeValue.Close, valBuffer, (startPosition + 4) * sizeof(double));
						EndianBitConverter.Big.CopyBytes(rangeValue.Sum, valBuffer, (startPosition + 5) * sizeof(double));
					}
				}

				tree.Add(time.Ticks, valBuffer);
			}

			public void Dispose()
			{
			}
		}

		public void Dispose()
		{
			if (disposed)
				return;

			// give it 3 seconds to complete requests
			for (int i = 0; i < 30 && Interlocked.Read(ref metricsTimeSeries.ConcurrentRequestsCount) > 0; i++)
			{
				Thread.Sleep(100);
			}

			AppDomain.CurrentDomain.ProcessExit -= ShouldDispose;
			AppDomain.CurrentDomain.DomainUnload -= ShouldDispose;

			disposed = true;

			var exceptionAggregator = new ExceptionAggregator(Log, "Could not properly dispose TimeSeriesStorage");

			if (storageEnvironment != null)
				exceptionAggregator.Execute(storageEnvironment.Dispose);

			if (metricsTimeSeries != null)
				exceptionAggregator.Execute(metricsTimeSeries.Dispose);

			exceptionAggregator.ThrowIfNeeded();
		}

		private void ShouldDispose(object sender, EventArgs eventArgs)
		{
			Dispose();
		}

		public TimeSeriesMetrics CreateMetrics()
		{
			var metrics = metricsTimeSeries;

			return new TimeSeriesMetrics
			{
				RequestsPerSecond = Math.Round(metrics.RequestsPerSecondCounter.CurrentValue, 3),
				Appends = metrics.Appends.CreateMeterData(),
				Deletes = metrics.Deletes.CreateMeterData(),
				ClientRequests = metrics.ClientRequests.CreateMeterData(),
				IncomingReplications = metrics.IncomingReplications.CreateMeterData(),
				OutgoingReplications = metrics.OutgoingReplications.CreateMeterData(),

				RequestsDuration = metrics.RequestDurationMetric.CreateHistogramData(),

				ReplicationBatchSizeMeter = metrics.ReplicationBatchSizeMeter.ToMeterDataDictionary(),
				ReplicationBatchSizeHistogram = metrics.ReplicationBatchSizeHistogram.ToHistogramDataDictionary(),
				ReplicationDurationHistogram = metrics.ReplicationDurationHistogram.ToHistogramDataDictionary(),
			};
		}

		public TimeSeriesStats CreateStats()
		{
			using (var reader = CreateReader())
			{
				var stats = new TimeSeriesStats
				{
					Name = Name,
					Url = TimeSeriesUrl,
					TimeSeriesCount = reader.GetTimeSeriesCount(),
					TimeSeriesSize = SizeHelper.Humane(TimeSeriesEnvironment.Stats().UsedDataFileSizeInBytes),
					RequestsPerSecond = Math.Round(metricsTimeSeries.RequestsPerSecondCounter.CurrentValue, 3),
				};
				return stats;
			}
		}

		public NotificationPublisher Publisher
		{
			get { return notificationPublisher; }
		}

		public StorageEnvironment TimeSeriesEnvironment
		{
			get { return storageEnvironment; }
		}

		public void CreatePrefixConfiguration(string prefix, byte valueLength)
		{
			var val = metadata.Read(PrefixesPrefix + prefix);
			if (val != null)
			{
				throw new InvalidOperationException(string.Format("Prefix {0} is already created", prefix));
			}
			metadata.Add(PrefixesPrefix + prefix, new[] {valueLength});
		}

		public void DeletePrefixConfiguration(string prefix)
		{
			var val = metadata.Read(PrefixesPrefix + prefix);
			if (val == null)
				throw new InvalidOperationException(string.Format("Prefix {0} does not exist", prefix));

			AssertNoExistDataForPrefix(prefix);
			metadata.Delete(PrefixesPrefix + prefix);
		}

		private void AssertNoExistDataForPrefix(string prefix)
		{
			throw new InvalidOperationException("Cannot delete prefix since there is associated data to it");
		}

		public void GetPrefixConfiguration(string prefix)
		{
			throw new NotImplementedException();
		}
	}
}