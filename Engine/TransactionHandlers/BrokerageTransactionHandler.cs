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
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Brokerages;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.TransactionHandlers
{
    /// <summary>
    /// Transaction handler for all brokerages
    /// </summary>
    public class BrokerageTransactionHandler : ITransactionHandler
    {
        private bool _exitTriggered;
        private IAlgorithm _algorithm;
        private IBrokerage _brokerage;
        private bool _syncedLiveBrokerageCashToday;

        // this value is used for determining how confident we are in our cash balance update
        private long _lastFillTimeTicks;
        private long _lastSyncTimeTicks;
        private readonly object _performCashSyncReentranceGuard = new object();
        private static readonly TimeSpan _liveBrokerageCashSyncTime = new TimeSpan(7, 45, 0); // 7:45 am

        /// <summary>
        /// OrderQueue holds the newly updated orders from the user algorithm waiting to be processed. Once
        /// orders are processed they are moved into the Orders queue awaiting the brokerage response.
        /// </summary>
        private ConcurrentQueue<OrderRequest> _orderRequestQueue;

        /// <summary>
        /// The orders dictionary holds orders which are sent to exchange, partially filled, completely filled or cancelled.
        /// Once the transaction thread has worked on them they get put here while witing for fill updates.
        /// </summary>
        private ConcurrentDictionary<int, Order> _orders;

        /// <summary>
        /// The orders tickets dictionary holds order tickets that the algorithm can use to reference a specific order. This
        /// includes invoking update and cancel commands. In the future, we can add more features to the ticket, such as events
        /// and async events (such as run this code when this order fills)
        /// </summary>
        private ConcurrentDictionary<int, OrderTicket> _orderTickets;

        private IResultHandler _resultHandler;
        private ManualResetEventSlim _processingCompletedEvent;

        /// <summary>
        /// Gets the permanent storage for all orders
        /// </summary>
        public ConcurrentDictionary<int, Order> Orders
        {
            get { return _orders; }
        }

        /// <summary>
        /// Gets the current number of orders that have been processed
        /// </summary>
        public int OrdersCount
        {
            get { return _orders.Count; }
        }

        /// <summary>
        /// Creates a new BrokerageTransactionHandler to process orders using the specified brokerage implementation
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="brokerage">The brokerage implementation to process orders and fire fill events</param>
        /// <param name="resultHandler"></param>
        public virtual void Initialize(IAlgorithm algorithm, IBrokerage brokerage, IResultHandler resultHandler)
        {
            if (brokerage == null)
            {
                throw new ArgumentNullException("brokerage");
            }

            // we don't need to do this today because we just initialized/synced
            _resultHandler = resultHandler;
            _syncedLiveBrokerageCashToday = true;
            _lastSyncTimeTicks = DateTime.Now.Ticks;

            _brokerage = brokerage;
            _brokerage.OrderStatusChanged += (sender, fill) =>
            {
                HandleOrderEvent(fill);
            };

            _brokerage.SecurityHoldingUpdated += (sender, holding) =>
            {
                HandleSecurityHoldingUpdated(holding);
            };

            _brokerage.AccountChanged += (sender, account) =>
            {
                HandleAccountChanged(account);
            };

            IsActive = true;

            _algorithm = algorithm;

            // also save off the various order data structures locally
            _orders = new ConcurrentDictionary<int, Order>();
            _orderRequestQueue = new ConcurrentQueue<OrderRequest>();
            _orderTickets = new ConcurrentDictionary<int, OrderTicket>();
            _processingCompletedEvent = new ManualResetEventSlim(true);
        }

        /// <summary>
        /// Boolean flag indicating the Run thread method is busy. 
        /// False indicates it is completely finished processing and ready to be terminated.
        /// </summary>
        public bool IsActive { get; private set; }

        /// <summary>
        /// Reset event that signals when this order processor is not busy processing orders
        /// </summary>
        public ManualResetEventSlim ProcessingCompletedEvent
        {
            get { return _processingCompletedEvent; }
        }

        #region Order Request Processing

        /// <summary>
        /// Adds the specified order to be processed
        /// </summary>
        /// <param name="request">The order to be processed</param>
        public OrderTicket Process(OrderRequest request)
        {
            switch (request.OrderRequestType)
            {
                case OrderRequestType.Submit:
                    return AddOrder((SubmitOrderRequest) request);

                case OrderRequestType.Update:
                    return UpdateOrder((UpdateOrderRequest) request);

                case OrderRequestType.Cancel:
                    return CancelOrder((CancelOrderRequest) request);

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Add an order to collection and return the unique order id or negative if an error.
        /// </summary>
        /// <param name="request">A request detailing the order to be submitted</param>
        /// <returns>New unique, increasing orderid</returns>
        public OrderTicket AddOrder(SubmitOrderRequest request)
        {
            request.SetResponse(OrderResponse.Success(request), OrderRequestStatus.Processing);
            var ticket = new OrderTicket(_algorithm.Transactions, request);
            _orderTickets.TryAdd(ticket.OrderId, ticket);

            // send the order to be processed after creating the ticket
            _orderRequestQueue.Enqueue(request);
            return ticket;
        }

        /// <summary>
        /// Update an order yet to be filled such as stop or limit orders.
        /// </summary>
        /// <param name="request">Request detailing how the order should be updated</param>
        /// <remarks>Does not apply if the order is already fully filled</remarks>
        public OrderTicket UpdateOrder(UpdateOrderRequest request)
        {
            OrderTicket ticket;
            if (!_orderTickets.TryGetValue(request.OrderId, out ticket))
            {
                return OrderTicket.InvalidUpdateOrderId(_algorithm.Transactions, request);
            }

            ticket.AddUpdateRequest(request);

            try
            {
                //Update the order from the behaviour
                var order = GetOrderByIdInternal(request.OrderId);
                if (order == null)
                {
                    // can't update an order that doesn't exist!
                    request.SetResponse(OrderResponse.UnableToFindOrder(request));
                }
                else if (order.Status.IsClosed())
                {
                    // can't update a completed order
                    request.SetResponse(OrderResponse.InvalidStatus(request, order));
                }
                else if (request.Quantity.HasValue && request.Quantity.Value == 0)
                {
                    request.SetResponse(OrderResponse.ZeroQuantity(request));
                }
                else
                {
                    request.SetResponse(OrderResponse.Success(request), OrderRequestStatus.Processing);
                    _orderRequestQueue.Enqueue(request);
                }
            }
            catch (Exception err)
            {
                Log.Error("Algorithm.Transactions.UpdateOrder(): " + err.Message);
                request.SetResponse(OrderResponse.Error(request, OrderResponseErrorCode.ProcessingError, err.Message));
            }

            return ticket;
        }

        /// <summary>
        /// Remove this order from outstanding queue: user is requesting a cancel.
        /// </summary>
        /// <param name="request">Request containing the specific order id to remove</param>
        public OrderTicket CancelOrder(CancelOrderRequest request)
        {
            OrderTicket ticket;
            if (!_orderTickets.TryGetValue(request.OrderId, out ticket))
            {
                Log.Error("BrokerageTransactionHandler.CancelOrder(): Unable to locate ticket for order.");
                return OrderTicket.InvalidCancelOrderId(_algorithm.Transactions, request);
            }

            ticket.SetCancelRequest(request);
            
            try
            {
                //Error check
                var order = GetOrderByIdInternal(request.OrderId);
                if (order == null)
                {
                    Log.Error("BrokerageTransactionHandler.CancelOrder(): Cannot find this id.");
                    request.SetResponse(OrderResponse.UnableToFindOrder(request));
                }
                else if (order.Status.IsClosed())
                {
                    Log.Error("BrokerageTransactionHandler.CancelOrder(): Order already filled");
                    request.SetResponse(OrderResponse.InvalidStatus(request, order));
                }
                else
                {
                    // send the request to be processed
                    request.SetResponse(OrderResponse.Success(request), OrderRequestStatus.Processing);
                    _orderRequestQueue.Enqueue(request);
                }
            }
            catch (Exception err)
            {
                Log.Error("TransactionManager.RemoveOrder(): " + err.Message);
                request.SetResponse(OrderResponse.Error(request, OrderResponseErrorCode.ProcessingError, err.Message));
            }

            return ticket;
        }

        /// <summary>
        /// Gets and enumerable of <see cref="OrderTicket"/> matching the specified <paramref name="filter"/>
        /// </summary>
        /// <param name="filter">The filter predicate used to find the required order tickets</param>
        /// <returns>An enumerable of <see cref="OrderTicket"/> matching the specified <paramref name="filter"/></returns>
        public IEnumerable<OrderTicket> GetOrderTickets(Func<OrderTicket, bool> filter = null)
        {
            return _orderTickets.Select(x => x.Value).Where(filter ?? (x => true));
        }

        #endregion

        /// <summary>
        /// Get the order by its id
        /// </summary>
        /// <param name="orderId">Order id to fetch</param>
        /// <returns>The order with the specified id, or null if no match is found</returns>
        public Order GetOrderById(int orderId)
        {
            Order order = GetOrderByIdInternal(orderId);
            return order != null ? order.Clone() : null;
        }

        private Order GetOrderByIdInternal(int orderId)
        {
            // this function can be invoked by brokerages when getting open orders, guard against null ref
            if (_orders == null) return null;
            
            Order order;
            return _orders.TryGetValue(orderId, out order) ? order : null;
        }

        /// <summary>
        /// Gets the order by its brokerage id
        /// </summary>
        /// <param name="brokerageId">The brokerage id to fetch</param>
        /// <returns>The first order matching the brokerage id, or null if no match is found</returns>
        public Order GetOrderByBrokerageId(int brokerageId)
        {
            // this function can be invoked by brokerages when getting open orders, guard against null ref
            if (_orders == null) return null;
            
            var order = _orders.FirstOrDefault(x => x.Value.BrokerId.Contains(brokerageId)).Value;
            return order != null ? order.Clone() : null;
        }

        /// <summary>
        /// Gets all orders matching the specified filter
        /// </summary>
        /// <param name="filter">Delegate used to filter the orders</param>
        /// <returns>All open orders this order provider currently holds</returns>
        public IEnumerable<Order> GetOrders(Func<Order, bool> filter = null)
        {
            if (_orders == null)
            {
                // this is the case when we haven't initialize yet, backtesting brokerage
                // will end up calling this through the transaction manager
                return Enumerable.Empty<Order>();
            }

            if (filter != null)
            {
                // return a clone to prevent object reference shenanigans, you must submit a request to change the order
                return _orders.Select(x => x.Value).Where(filter).Select(x => x.Clone());
            }
            return _orders.Select(x => x.Value).Select(x => x.Clone());
        }

        /// <summary>
        /// Primary thread entry point to launch the transaction thread.
        /// </summary>
        public void Run()
        {
            while (!_exitTriggered)
            {
                _processingCompletedEvent.Reset();

                OrderRequest request;
                if (!_orderRequestQueue.TryDequeue(out request))
                {
                    _processingCompletedEvent.Set();

                    // if it's empty just sleep this thread for a little bit
                    Thread.Sleep(1);
                    continue;
                }

                OrderResponse response;
                switch (request.OrderRequestType)
                {
                    case OrderRequestType.Submit:
                        response = HandleSubmitOrderRequest((SubmitOrderRequest) request);
                        break;
                    case OrderRequestType.Update:
                        response = HandleUpdateOrderRequest((UpdateOrderRequest) request);
                        break;
                    case OrderRequestType.Cancel:
                        response = HandleCancelOrderRequest((CancelOrderRequest) request);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // we've finally finished processing the request, mark as processed
                request.SetResponse(response, OrderRequestStatus.Processed);

                ProcessAsynchronousEvents();
            }

            Log.Trace("BrokerageTransactionHandler.Run(): Ending Thread...");
            IsActive = false;
        }

        /// <summary>
        /// Processes asynchronous events on the transaction handler's thread
        /// </summary>
        public virtual void ProcessAsynchronousEvents()
        {
            // NOP
        }

        /// <summary>
        /// Processes all synchronous events that must take place before the next time loop for the algorithm
        /// </summary>
        public virtual void ProcessSynchronousEvents()
        {
            // how to do synchronous market orders for real brokerages?

            // in backtesting we need to wait for orders to be removed from the queue and finished processing
            if (!_algorithm.LiveMode)
            {
                var spinWait = new SpinWait();
                while (!_orderRequestQueue.IsEmpty)
                {
                    // spin wait until the queue is empty
                    spinWait.SpinOnce();
                }
                // now wait for completed processing to signal
                _processingCompletedEvent.Wait();
                return;
            }

            Log.Debug("BrokerageTransactionHandler.ProcessSynchronousEvents(): Enter");

            // every morning flip this switch back
            if (_syncedLiveBrokerageCashToday && DateTime.Now.Date != LastSyncDate)
            {
                _syncedLiveBrokerageCashToday = false;
            }

            // we want to sync up our cash balance before market open
            if (_algorithm.LiveMode && !_syncedLiveBrokerageCashToday && DateTime.Now.TimeOfDay >= _liveBrokerageCashSyncTime)
            {
                try
                {
                    // only perform cash syncs if we haven't had a fill for at least 10 seconds
                    if (TimeSinceLastFill > TimeSpan.FromSeconds(10))
                    {
                        PerformCashSync();
                    }
                }
                catch (Exception err)
                {
                    Log.Error(err, "Updating cash balances");
                }
            }

            // we want to remove orders older than 10k records, but only in live mode
            const int maxOrdersToKeep = 10000;
            if (_orders.Count < maxOrdersToKeep + 1) return;

            int max = _orders.Max(x => x.Key);
            int lowestOrderIdToKeep = max - maxOrdersToKeep;
            foreach (var item in _orders.Where(x => x.Key <= lowestOrderIdToKeep))
            {
                Order value;
                OrderTicket ticket;
                _orders.TryRemove(item.Key, out value);
                _orderTickets.TryRemove(item.Key, out ticket);
            }

            Log.Debug("BrokerageTransactionHandler.ProcessSynchronousEvents(): Exit");
        }

        /// <summary>
        /// Syncs cash from brokerage with portfolio object
        /// </summary>
        private void PerformCashSync()
        {
            try
            {
                // prevent reentrance in this method
                if (!Monitor.TryEnter(_performCashSyncReentranceGuard))
                {
                    return;
                }

                Log.Trace("BrokerageTransactionHandler.PerformCashSync(): Sync cash balance");

                var balances = new List<Cash>();
                try
                {
                    balances = _brokerage.GetCashBalance();
                }
                catch (Exception err)
                {
                    Log.Error(err);
                }

                if (balances.Count == 0)
                {
                    return;
                }
                
                // if we were returned our balances, update everything and flip our flag as having performed sync today
                foreach (var balance in balances)
                {
                    Cash cash;
                    if (_algorithm.Portfolio.CashBook.TryGetValue(balance.Symbol, out cash))
                    {
                        // compare in dollars
                        var delta = cash.Quantity - balance.Quantity;
                        if (Math.Abs(delta) > _algorithm.Portfolio.CashBook.ConvertToAccountCurrency(delta, cash.Symbol))
                        {
                            // log the delta between 
                            Log.LogHandler.Trace("BrokerageTransactionHandler.PerformCashSync(): {0} Delta: {1}", balance.Symbol,
                                delta.ToString("0.00"));
                        }
                    }
                    _algorithm.Portfolio.SetCash(balance.Symbol, balance.Quantity, balance.ConversionRate);
                }

                _syncedLiveBrokerageCashToday = true;
            }
            finally
            {
                Monitor.Exit(_performCashSyncReentranceGuard);
            }

            // fire off this task to check if we've had recent fills, if we have then we'll invalidate the cash sync
            // and do it again until we're confident in it
            Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
            {
                // we want to make sure this is a good value, so check for any recent fills
                if (TimeSinceLastFill <= TimeSpan.FromSeconds(20))
                {
                    // this will cause us to come back in and reset cash again until we 
                    // haven't processed a fill for +- 10 seconds of the set cash time
                    _syncedLiveBrokerageCashToday = false;
                    Log.Trace("BrokerageTransactionHandler.PerformCashSync(): Unverified cash sync - resync required.");
                }
                else
                {
                    _lastSyncTimeTicks = DateTime.Now.Ticks;
                    Log.Trace("BrokerageTransactionHandler.PerformCashSync(): Verified cash sync.");
                }
            });
        }

        /// <summary>
        /// Signal a end of thread request to stop montioring the transactions.
        /// </summary>
        public void Exit()
        {
            _exitTriggered = true;
        }

        /// <summary>
        /// Handles a request to submit a new order
        /// </summary>
        private OrderResponse HandleSubmitOrderRequest(SubmitOrderRequest request)
        {
            OrderTicket ticket;
            var order = Order.CreateOrder(request);

            if (!_orders.TryAdd(order.Id, order))
            {
                Log.Error("BrokerageTransactionHandler.HandleSubmitOrderRequest(): Unable to add new order, order not processed.");
                return OrderResponse.Error(request, OrderResponseErrorCode.OrderAlreadyExists, "Cannot process submit request because order with id {0} already exists");
            }
            if (!_orderTickets.TryGetValue(order.Id, out ticket))
            {
                Log.Error("BrokerageTransactionHandler.HandleSubmitOrderRequest(): Unable to retrieve order ticket, order not processed.");
                return OrderResponse.UnableToFindOrder(request);
            }

            // update the ticket's internal storage with this new order reference
            ticket.SetOrder(order);

            // check to see if we have enough money to place the order
            if (!_algorithm.Transactions.GetSufficientCapitalForOrder(_algorithm.Portfolio, order))
            {
                order.Status = OrderStatus.Invalid;
                var security = _algorithm.Securities[order.Symbol];
                var response = OrderResponse.Error(request, OrderResponseErrorCode.InsufficientBuyingPower, string.Format("Order Error: id: {0}, Insufficient buying power to complete order (Value:{1}).", order.Id, order.GetValue(security.Price).SmartRounding()));
                _algorithm.Error(response.ErrorMessage);
                return response;
            }

            // verify that our current brokerage can actually take the order
            BrokerageMessageEvent message;
            if (!_algorithm.LiveMode && !_algorithm.BrokerageModel.CanSubmitOrder(_algorithm.Securities[order.Symbol], order, out message))
            {
                // if we couldn't actually process the order, mark it as invalid and bail
                order.Status = OrderStatus.Invalid;
                if (message == null) message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "InvalidOrder", "BrokerageModel declared unable to submit order: " + order.Id);
                var response = OrderResponse.Error(request, OrderResponseErrorCode.BrokerageModelRefusedToSubmitOrder, "OrderID: " + order.Id + " " + message);
                _algorithm.Error(response.ErrorMessage);
                return response;
            }

            // set the order status based on whether or not we successfully submitted the order to the market
            bool orderPlaced;
            try
            {
                orderPlaced = _brokerage.PlaceOrder(order);
            }
            catch (Exception err)
            {
                Log.Error(err);
                orderPlaced = false;
             }

            if (orderPlaced)
            {
                order.Status = OrderStatus.Submitted;
            }
            else
            {
                order.Status = OrderStatus.Invalid;
                var response = OrderResponse.Error(request, OrderResponseErrorCode.BrokerageFailedToSubmitOrder, "Brokerage failed to place order: " + order.Id);
                _algorithm.Error(response.ErrorMessage);
                return response;
            }
            
            order.Status = OrderStatus.Submitted;
            return OrderResponse.Success(request);
        }

        /// <summary>
        /// Handles a request to update order properties
        /// </summary>
        private OrderResponse HandleUpdateOrderRequest(UpdateOrderRequest request)
        {
            Order order;
            OrderTicket ticket;
            if (!_orders.TryGetValue(request.OrderId, out order) || !_orderTickets.TryGetValue(request.OrderId, out ticket))
            {
                Log.Error("BrokerageTransactionHandler.HandleUpdateOrderRequest(): Unable to update order with ID " + request.OrderId);
                return OrderResponse.UnableToFindOrder(request);
            }
            
            if (!CanUpdateOrder(order))
            {
                return OrderResponse.InvalidStatus(request, order);
            }

            // modify the values of the order object
            order.ApplyUpdateOrderRequest(request);
            ticket.SetOrder(order);

            bool orderUpdated;
            try
            {
                orderUpdated = _brokerage.UpdateOrder(order);
            }
            catch (Exception err)
            {
                Log.Error(err);
                orderUpdated = false;
            }
            
            if (!orderUpdated)
            {
                // we failed to update the order for some reason
                order.Status = OrderStatus.Invalid;
                return OrderResponse.Error(request, OrderResponseErrorCode.BrokerageFailedToUpdateOrder, "Brokerage failed to update order with id " + request.OrderId);
            }

            return OrderResponse.Success(request);
        }

        /// <summary>
        /// Returns true if the specified order can be updated
        /// </summary>
        /// <param name="order">The order to check if we can update</param>
        /// <returns>True if the order can be updated, false otherwise</returns>
        private bool CanUpdateOrder(Order order)
        {
            return order.Status != OrderStatus.Filled
                && order.Status != OrderStatus.Canceled
                && order.Status != OrderStatus.PartiallyFilled
                && order.Status != OrderStatus.Invalid;
        }

        /// <summary>
        /// Handles a request to cancel an order
        /// </summary>
        private OrderResponse HandleCancelOrderRequest(CancelOrderRequest request)
        {
            Order order;
            OrderTicket ticket;
            if (!_orders.TryGetValue(request.OrderId, out order) || !_orderTickets.TryGetValue(request.OrderId, out ticket))
            {
                Log.Error("BrokerageTransactionHandler.HandleCancelOrderRequest(): Unable to cancel order with ID " + request.OrderId + ".");
                return OrderResponse.UnableToFindOrder(request);
            }
            
            if (order.Status.IsClosed())
            {
                return OrderResponse.InvalidStatus(request, order);
            }

            ticket.SetOrder(order);

            bool orderCanceled;
            try
            {
                orderCanceled = _brokerage.CancelOrder(order);
            }
            catch (Exception err)
            {
                Log.Error(err);
                orderCanceled = false;
            }

            if (!orderCanceled)
            {
                // we failed to cancel the order for some reason
                order.Status = OrderStatus.Invalid;
            }
            else
            {
                // we succeeded to cancel the order
                order.Status = OrderStatus.Canceled;
            }

            if (request.Tag != null)
            {
                // update the tag, useful for 'why' we canceled the order
                order.Tag = request.Tag;
            }

            return OrderResponse.Success(request);
        }

        private void HandleOrderEvent(OrderEvent fill)
        {
            // update the order status
            var order = GetOrderByIdInternal(fill.OrderId);
            if (order == null)
            {
                Log.Error("BrokerageTransactionHandler.HandleOrderEvent(): Unable to locate Order with id " + fill.OrderId);
                return;
            }

            // set the status of our order object based on the fill event
            order.Status = fill.Status;

            // save that the order event took place, we're initializing the list with a capacity of 2 to reduce number of mallocs
            //these hog memory
            //List<OrderEvent> orderEvents = _orderEvents.GetOrAdd(orderEvent.OrderId, i => new List<OrderEvent>(2));
            //orderEvents.Add(orderEvent);

            //Apply the filled order to our portfolio:
            if (fill.Status == OrderStatus.Filled || fill.Status == OrderStatus.PartiallyFilled)
            {
                Log.Debug("BrokerageTransactionHandler.HandleOrderEvent(): " + fill);
                Interlocked.Exchange(ref _lastFillTimeTicks, DateTime.Now.Ticks);
                _algorithm.Portfolio.ProcessFill(fill);
            }

            // update the ticket after we've processed the fill, but before the event, this way everything is ready for user code
            OrderTicket ticket;
            if (_orderTickets.TryGetValue(fill.OrderId, out ticket))
            {
                ticket.AddOrderEvent(fill);
            }
            else
            {
                Log.Error("BrokerageTransactionHandler.HandleOrderEvent(): Unable to resolve ticket: " + fill.OrderId);
            }

            //We have an event! :) Order filled, send it in to be handled by algorithm portfolio.
            if (fill.Status != OrderStatus.None) //order.Status != OrderStatus.Submitted
            {
                //Create new order event:
                _resultHandler.OrderEvent(fill);
                try
                {
                    //Trigger our order event handler
                    _algorithm.OnOrderEvent(fill);
                }
                catch (Exception err)
                {
                    _algorithm.Error("Order Event Handler Error: " + err.Message);
                }
            }
        }

        /// <summary>
        /// Brokerages can send account updates, this include cash balance updates. Since it is of
        /// utmost important to always have an accurate picture of reality, we'll trust this information
        /// as truth
        /// </summary>
        private void HandleAccountChanged(AccountEvent account)
        {
            // how close are we?
            var delta = _algorithm.Portfolio.CashBook[account.CurrencySymbol].Quantity - account.CashBalance;
            if (delta != 0)
            {
                Log.Trace(string.Format("BrokerageTransactionHandler.HandleAccountChanged(): {0} Cash Delta: {1}", account.CurrencySymbol, delta));
            }

            // we don't actually want to do this, this data can be delayed
            // override the current cash value to we're always gauranted to be in sync with the brokerage's push updates
            //_algorithm.Portfolio.CashBook[account.CurrencySymbol].Quantity = account.CashBalance;
        }

        /// <summary>
        /// Brokerages can send portfolio updates which should include average price of holdings and the
        /// quantity of holdings, we'll trust this information as truth and just set the portfolio with it
        /// </summary>
        private void HandleSecurityHoldingUpdated(SecurityEvent holding)
        {
            // how close are we?
            var securityHolding = _algorithm.Portfolio[holding.Symbol];
            var deltaQuantity = securityHolding.Quantity - holding.Quantity;
            var deltaAvgPrice = securityHolding.AveragePrice - holding.AveragePrice;
            if (deltaQuantity != 0 || deltaAvgPrice != 0)
            {
                Log.Trace(string.Format("BrokerageTransactionHandler.HandleSecurityHoldingUpdated(): {0} DeltaQuantity: {1} DeltaAvgPrice: {2}", holding.Symbol, deltaQuantity, deltaAvgPrice));
            }

            // we don't actually want to do this, this data can be delayed
            //securityHolding.SetHoldings(holding.AveragePrice, holding.Quantity);
        }

        /// <summary>
        /// Gets the amount of time since the last call to algorithm.Portfolio.ProcessFill(fill)
        /// </summary>
        private TimeSpan TimeSinceLastFill
        {
            get { return DateTime.Now - new DateTime(Interlocked.Read(ref _lastFillTimeTicks)); }
        }

        /// <summary>
        /// Gets the date of the last sync
        /// </summary>
        private DateTime LastSyncDate
        {
            get { return new DateTime(Interlocked.Read(ref _lastSyncTimeTicks)).Date; }
        }
    }
}
