﻿/*
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.Lean.Engine.RealTime
{
    /// <summary>
    /// Live trading realtime event processing.
    /// </summary>
    public class LiveTradingRealTimeHandler : IRealTimeHandler
    {
        private DateTime _time;
        private bool _exitTriggered;
        private bool _isActive = true;
        private List<RealTimeEvent> _events;
        private Dictionary<SecurityType, MarketToday> _today;
        private IDataFeed _feed;
        private TimeSpan _endOfDayDelta = TimeSpan.FromMinutes(10);

        //Algorithm and Handlers:
        private IAlgorithm _algorithm;
        private IResultHandler _resultHandler;
        private IApi _api;

        /// <summary>
        /// Current time.
        /// </summary>
        public DateTime Time
        {
            get
            {
                return _time;
            }
        }

        /// <summary>
        /// Boolean flag indicating thread state.
        /// </summary>
        public bool IsActive
        {
            get
            {
                return _isActive;
            }
        }

        /// <summary>
        /// List of the events to trigger.
        /// </summary>
        public List<RealTimeEvent> Events
        {
            get
            {
                return _events;
            }
        }

        /// <summary>
        /// Market hours for today for each security type in the algorithm
        /// </summary>
        public Dictionary<SecurityType, MarketToday> MarketToday
        {
            get
            {
                return _today;
            }
        }

        /// <summary>
        /// Intializes the real time handler for the specified algorithm and job
        /// </summary>
        public void Initialize(IAlgorithm algorithm, AlgorithmNodePacket job, IResultHandler resultHandler, IApi api)
        {
            //Initialize:
            _algorithm = algorithm;
            _events = new List<RealTimeEvent>();
            _today = new Dictionary<SecurityType, MarketToday>();
            _resultHandler = resultHandler;
            _api = api;
        }

        /// <summary>
        /// Execute the live realtime event thread montioring. 
        /// It scans every second monitoring for an event trigger.
        /// </summary>
        public void Run()
        {
            //Initialize to today time:
            _isActive = true;

            //Continue looping until exit triggered:
            while (!_exitTriggered)
            {
                //Trigger as close to second as reasonable. 1.00, 2.00 etc.
                _time = DateTime.UtcNow.ConvertToUtc(_algorithm.TimeZone);
                var nextSecond = _time.RoundUp(TimeSpan.FromSeconds(1));
                var delay = Convert.ToInt32((nextSecond - _time).TotalMilliseconds);
                Thread.Sleep(delay < 0 ? 1 : delay);

                //Refresh event processing:
                ScanEvents();
            }

            _isActive = false;
            Log.Trace("LiveTradingRealTimeHandler.Run(): Exiting thread... Exit triggered: " + _exitTriggered);
        }

        /// <summary>
        /// Set up the realtime event handlers for today.
        /// </summary>
        /// <remarks>
        ///     We setup events for:to 
        ///     - Refreshing of the brokerage session tokens.
        ///     - Getting the new daily market open close times.
        ///     - Setting up the "OnEndOfDay" events which close -10min before closing.
        /// </remarks>
        /// <param name="date">Datetime today</param>
        public void SetupEvents(DateTime date)
        {
            try
            {
                //Clear the previous days events to reset with today:
                ClearEvents();
                
                //Refresh the market hours store:
                RefreshMarketHoursToday(date);

                // END OF DAY REAL TIME EVENT:
                SetupEndOfDayEvent();
            }
            catch (Exception err)
            {
                Log.Error("LiveTradingRealTimeHandler.SetupEvents(): " + err.Message);
            }
        }

        /// <summary>
        /// Setup the end of day event handler for all symbols in the users algorithm. 
        /// End of market hours are determined by the market hours today property.
        /// </summary>
        private void SetupEndOfDayEvent()
        {
            // Load Today variables based on security type:
            foreach (var security in _algorithm.Securities.Values)
            {
                DateTime? endOfDayEventTime = null;

                if (!security.IsDynamicallyLoadedData)
                {
                    //If the market is open, set the end of day event time:
                    if (_today[security.Type].Status == "open")
                    {
                        endOfDayEventTime = _today[security.Type].Open.End.Subtract(_endOfDayDelta);
                    }
                }
                else
                {
                    //If custom/dynamic data get close time from user defined security object.
                    endOfDayEventTime = _time.Date + security.Exchange.MarketClose.Subtract(_endOfDayDelta);
                }

                //2. Set this time as the handler for EOD event:
                if (endOfDayEventTime.HasValue)
                {

                    if (_time< endOfDayEventTime.Value)
                    {

                        Log.Trace(string.Format("LiveTradingRealTimeHandler.SetupEvents(): Setup EOD Event for {0}", endOfDayEventTime.Value.ToString("u")));
                    }
                    else
                    {

                        Log.Trace(string.Format("LiveTradingRealTimeHandler.SetupEvents(): Skipping Setup of EOD Event for {0} because time has passed.", security.Symbol));
                        continue;
                    }

                    var symbol = security.Symbol;
                    AddEvent(new RealTimeEvent(endOfDayEventTime.Value, () =>
                    {
                        try
                        {
                            _algorithm.OnEndOfDay(symbol);
                            Log.Trace(string.Format("LiveTradingRealTimeHandler: Fired On End of Day Event({0}) for Day({1})", symbol, _time.ToShortDateString()));
                        }
                        catch (Exception err)
                        {
                            _resultHandler.RuntimeError("Runtime error in OnEndOfDay event: " + err.Message, err.StackTrace);
                            Log.Error("LiveTradingRealTimeHandler.SetupEvents.Trigger OnEndOfDay(): " + err.Message);
                        }
                    }, true));
                }
            }

            // fire just before the day rolls over, 11:58pm
            var endOfDay = _time.Date.AddHours(23.967);
            if (_time < endOfDay) 
            { 
                AddEvent(new RealTimeEvent(endOfDay, () =>
                {
                    try
                    {
                        _algorithm.OnEndOfDay();
                        Log.Trace(string.Format("LiveTradingRealTimeHandler: Fired On End of Day Event() for Day({0})", _time.ToShortDateString()));
                    }
                    catch (Exception err)
                    {
                        _resultHandler.RuntimeError("Runtime error in OnEndOfDay event: " + err.Message, err.StackTrace);
                        Log.Error("LiveTradingRealTimeHandler.SetupEvents.Trigger OnEndOfDay(): " + err.Message);
                    }
                }, true));
            }
        }

        /// <summary>
        /// Refresh the Today variable holding the market hours information
        /// </summary>
        private void RefreshMarketHoursToday(DateTime date)
        {
            _today.Clear();

            //Setup the Security Open Close Market Hours:
            foreach (var sub in _algorithm.SubscriptionManager.Subscriptions)
            {
                var security = _algorithm.Securities[sub.Symbol];

                //Get the data for this asset from the QC API.
                if (!_today.ContainsKey(security.Type))
                {
                    //Setup storage
                    _today.Add(security.Type, new MarketToday());
                    //Refresh the market information
                    _today[security.Type] = _api.MarketToday(date, security.Type);
                    Log.Trace(
                        string.Format(
                            "LiveTradingRealTimeHandler.SetupEvents(): Daily Market Hours Setup for Security Type: {0} Start: {1} Stop: {2}",
                            security.Type, _today[security.Type].Open.Start, _today[security.Type].Open.End));
                }

                //Based on the type of security, set the market hours information for the exchange class.
                switch (security.Type)
                {
                    case SecurityType.Equity:
                        var equityMarketHours = _today[SecurityType.Equity];
                        if (equityMarketHours.Status != "open")
                        {
                            _algorithm.Securities[sub.Symbol].Exchange.SetMarketHours(TimeSpan.Zero, TimeSpan.Zero, date.DayOfWeek);
                        }
                        else
                        {
                            var extendedMarketOpen = equityMarketHours.PreMarket.Start.TimeOfDay;
                            var marketOpen = equityMarketHours.Open.Start.TimeOfDay;
                            var marketClose = equityMarketHours.Open.End.TimeOfDay;
                            var extendedMarketClose = equityMarketHours.PostMarket.End.TimeOfDay;
                            _algorithm.Securities[sub.Symbol].Exchange.SetMarketHours(extendedMarketOpen, marketOpen, marketClose, extendedMarketClose, date.DayOfWeek);
                            Log.Trace(string.Format("LiveTradingRealTimeHandler.SetupEvents(Equity): Market hours set: Symbol: {0} Extended Start: {1} Start: {2} End: {3} Extended End: {4}",
                                    sub.Symbol, extendedMarketOpen, marketOpen, marketClose, extendedMarketClose));
                        }
                        break;

                    case SecurityType.Forex:
                        var forexMarketHours = _today[SecurityType.Forex].Open;
                        _algorithm.Securities[sub.Symbol].Exchange.SetMarketHours(forexMarketHours.Start.TimeOfDay, forexMarketHours.End.TimeOfDay, date.DayOfWeek);
                        Log.Trace(string.Format("LiveTradingRealTimeHandler.SetupEvents(Forex): Normal market hours set: Symbol: {0} Start: {1} End: {2}",
                                sub.Symbol, forexMarketHours.Start, forexMarketHours.End));
                        break;
                }
            }
        }

        /// <summary>
        /// Container for all time based events.
        /// </summary>
        public void ScanEvents()
        {
            for (var i = 0; i < _events.Count; i++)
            {
                _events[i].Scan(_time);
            }
        }

        /// <summary>
        /// Add this new event to our list.
        /// </summary>
        /// <param name="newEvent">New event we'd like processed.</param>
        public void AddEvent(RealTimeEvent newEvent)
        {
            _events.Add(newEvent);
        }

        /// <summary>
        /// Reset the events -- 
        /// All real time event handlers are self-resetting, and much auto-trigger a reset when the day changes.
        /// </summary>
        public void ResetEvents()
        {
            for (var i = 0; i < _events.Count; i++)
            {
                _events[i].Reset();
            }
        }

        /// <summary>
        /// Clear any outstanding events fom processing list.
        /// </summary>
        public void ClearEvents()
        {
            _events.Clear();
        }

        /// <summary>
        /// Set the current time. If the date changes re-start the realtime event setup routines.
        /// </summary>
        /// <param name="time"></param>
        public void SetTime(DateTime time)
        {
            //Reset all the daily events
            if (_time.Date != time.Date)
            {
                //Each day needs the events reset to update the market hours and set daily targets/events.
                SetupEvents(time);
            }
        }

        /// <summary>
        /// Stop the real time thread
        /// </summary>
        public void Exit()
        {
            _exitTriggered = true;
        }
    } // End Result Handler Thread:

} // End Namespace
