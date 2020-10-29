/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories
{
    /// <summary>
    /// Provides an implementation of <see cref="ISubscriptionEnumeratorFactory"/> that used the <see cref="SubscriptionDataReader"/>
    /// </summary>
    /// <remarks>Only used on backtesting by the <see cref="FileSystemDataFeed"/></remarks>
    public class SubscriptionDataReaderSubscriptionEnumeratorFactory : ISubscriptionEnumeratorFactory, IDisposable
    {
        private readonly bool _isLiveMode;
        private readonly IResultHandler _resultHandler;
        private readonly IFactorFileProvider _factorFileProvider;
        private readonly ZipDataCacheProvider _zipDataCacheProvider;
        private readonly ConcurrentDictionary<Symbol, string> _numericalPrecisionLimitedWarnings;
        private readonly int _numericalPrecisionLimitedWarningsMaxCount = 10;
        private readonly ConcurrentDictionary<Symbol, string> _startDateLimitedWarnings;
        private readonly int _startDateLimitedWarningsMaxCount = 10;
        private readonly Func<SubscriptionRequest, IEnumerable<DateTime>> _tradableDaysProvider;
        private readonly IMapFileProvider _mapFileProvider;
        private readonly bool _enablePriceScaling;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionDataReaderSubscriptionEnumeratorFactory"/> class
        /// </summary>
        /// <param name="resultHandler">The result handler for the algorithm</param>
        /// <param name="mapFileProvider">The map file provider</param>
        /// <param name="factorFileProvider">The factor file provider</param>
        /// <param name="dataProvider">Provider used to get data when it is not present on disk</param>
        /// <param name="tradableDaysProvider">Function used to provide the tradable dates to be enumerator.
        /// Specify null to default to <see cref="SubscriptionRequest.TradableDays"/></param>
        /// <param name="enablePriceScaling">Applies price factor</param>
        public SubscriptionDataReaderSubscriptionEnumeratorFactory(IResultHandler resultHandler,
            IMapFileProvider mapFileProvider,
            IFactorFileProvider factorFileProvider,
            IDataProvider dataProvider,
            Func<SubscriptionRequest, IEnumerable<DateTime>> tradableDaysProvider = null,
            bool enablePriceScaling = true
            )
        {
            _resultHandler = resultHandler;
            _mapFileProvider = mapFileProvider;
            _factorFileProvider = factorFileProvider;
            _zipDataCacheProvider = new ZipDataCacheProvider(dataProvider, isDataEphemeral: false);
            _numericalPrecisionLimitedWarnings = new ConcurrentDictionary<Symbol, string>();
            _startDateLimitedWarnings = new ConcurrentDictionary<Symbol, string>();
            _isLiveMode = false;
            _tradableDaysProvider = tradableDaysProvider ?? (request => request.TradableDays);
            _enablePriceScaling = enablePriceScaling;
        }

        /// <summary>
        /// Creates a <see cref="SubscriptionDataReader"/> to read the specified request
        /// </summary>
        /// <param name="request">The subscription request to be read</param>
        /// <param name="dataProvider">Provider used to get data when it is not present on disk</param>
        /// <returns>An enumerator reading the subscription request</returns>
        public IEnumerator<BaseData> CreateEnumerator(SubscriptionRequest request, IDataProvider dataProvider)
        {
            var mapFileResolver = request.Configuration.TickerShouldBeMapped()
                                    ? _mapFileProvider.Get(request.Security.Symbol.ID.Market)
                                    : MapFileResolver.Empty;

            var dataReader = new SubscriptionDataReader(request.Configuration,
                request.StartTimeLocal,
                request.EndTimeLocal,
                mapFileResolver,
                _factorFileProvider,
                _tradableDaysProvider(request),
                _isLiveMode,
                 _zipDataCacheProvider,
                dataProvider
                );

            dataReader.InvalidConfigurationDetected += (sender, args) => { _resultHandler.ErrorMessage(args.Message); };
            dataReader.StartDateLimited += (sender, args) =>
            {
                // Queue this warning into our dictionary to report on dispose
                if (_startDateLimitedWarnings.Count <= _startDateLimitedWarningsMaxCount)
                {
                    _startDateLimitedWarnings.TryAdd(args.Symbol, args.Message);
                }
            };
            dataReader.DownloadFailed += (sender, args) => { _resultHandler.ErrorMessage(args.Message, args.StackTrace); };
            dataReader.ReaderErrorDetected += (sender, args) => { _resultHandler.RuntimeError(args.Message, args.StackTrace); };
            dataReader.NumericalPrecisionLimited += (sender, args) =>
            {
                // Set a hard limit to keep this warning list from getting unnecessarily large
                if (_numericalPrecisionLimitedWarnings.Count <= _numericalPrecisionLimitedWarningsMaxCount)
                {
                    _numericalPrecisionLimitedWarnings.TryAdd(args.Symbol, args.Message);
                }
            };

            var result = CorporateEventEnumeratorFactory.CreateEnumerators(
                dataReader,
                request.Configuration,
                _factorFileProvider,
                dataReader,
                mapFileResolver,
                request.StartTimeLocal,
                _enablePriceScaling);

            if (request.Security.Symbol.Value.EndsWith("#"))
            {
                // We remap the symbol of the data to the continuous contract & TODO: scale -> same for history
                result = new ContinuousContractEnumerator(result, request.Security.Symbol);
            }

            return result;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            // Log our numerical precision limited warnings if any
            if (!_numericalPrecisionLimitedWarnings.IsNullOrEmpty())
            {
                var message = "Due to numerical precision issues in the factor file, data for the following" +
                    $" symbols was adjust to a later starting date: {string.Join(", ", _numericalPrecisionLimitedWarnings.Values.Take(_numericalPrecisionLimitedWarningsMaxCount))}";

                // If we reached our max warnings count suggest that more may have been left out
                if (_numericalPrecisionLimitedWarnings.Count >= _numericalPrecisionLimitedWarningsMaxCount)
                {
                    message += "...";
                }

                _resultHandler.DebugMessage(message);
            }

            // Log our start date adjustments because of map files
            if (!_startDateLimitedWarnings.IsNullOrEmpty())
            {
                var message = "The starting dates for the following symbols have been adjusted to match their" +
                    $" map files first date: {string.Join(", ", _startDateLimitedWarnings.Values.Take(_startDateLimitedWarningsMaxCount))}";

                // If we reached our max warnings count suggest that more may have been left out
                if (_startDateLimitedWarnings.Count >= _startDateLimitedWarningsMaxCount)
                {
                    message += "...";
                }

                _resultHandler.DebugMessage(message);
            }

            _zipDataCacheProvider?.DisposeSafely();
        }
    }

    public class ContinuousContractEnumerator : IEnumerator<BaseData>
    {
        private IEnumerator<BaseData> _underlying;
        private Symbol _continuousContract;

        public BaseData Current { get; private set; }

        object IEnumerator.Current => Current;

        public ContinuousContractEnumerator(IEnumerator<BaseData> underlying, Symbol continuousContract)
        {
            _underlying = underlying;
            _continuousContract = continuousContract;
        }

        public bool MoveNext()
        {
            var result = _underlying.MoveNext();

            if (_underlying.Current != null)
            {
                Current = _underlying.Current.Clone(false);
                Current.Symbol = _continuousContract;
            }

            return result;
        }

        public void Reset()
        {
            _underlying.Reset();
        }

        public void Dispose()
        {
            _underlying.Dispose();
        }
    }
}
