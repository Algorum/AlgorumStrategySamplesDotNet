using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Algorum.Strategy.IndexFuturesTrend
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   public class IndexFuturesTrendStrategy : QuantEngineClient
   {
      private class State
      {
         public bool Bought;
         public TickData LastTick;
         public TickData CurrentTick;
         public TickData IdxCurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public bool DayChanged;
         public bool Short;
         public bool BreakInMonth;
         public double MonthPL;
         public TickData PrevTick;
         public bool OffsetOrder;
         public bool ProcessingOrder;
      }

      public const double Capital = 100000;
      private const double Leverage = 1; // 1x Leverage on margin by Brokerage

      private Symbol _symbolCurrentMonth;
      private Symbol _symbol;
      private State _state = null;
      private IIndicatorEvaluator _indicatorEvaluator = null;

      /// <summary>
      /// Helps create IndexFuturesTrendStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <param name="userId">User unique id</param>
      /// <returns>Instance of IndexFuturesTrendStrategy class</returns>
      public static async Task<IndexFuturesTrendStrategy> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new IndexFuturesTrendStrategy( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private IndexFuturesTrendStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
         : base( url, apiKey, launchMode, sid, userId )
      {
         // No-Op
      }

      private async Task InitializeAsync()
      {
         // Load any saved state
         _state = await GetDataAsync<State>( "state" );

         if ( ( _state == null ) || ( LaunchMode == StrategyLaunchMode.Backtesting ) )
         {
            _state = new State();
            _state.Orders = new List<Order>();
         }

         // Create our current month futures symbol object
         _symbolCurrentMonth = new Symbol()
         {
            SymbolType = SymbolType.FuturesIndex,
            FNOMonth = 0,
            FNOPeriodType = FNOPeriodType.Monthly,
            Ticker = "NIFTY"
         };

         // Create our Index symbol object
         _symbol = new Symbol()
         {
            SymbolType = SymbolType.Index,
            Ticker = "NIFTY 50"
         };

         _indicatorEvaluator = await CreateIndicatorEvaluatorAsync( new CreateIndicatorRequest()
         {
            Symbol = _symbol,
            CandlePeriod = CandlePeriod.Minute,
            PeriodSpan = 1
         } );

         // Subscribe to the symbols we want (one second tick data)
         await SubscribeSymbolsAsync( new List<Symbol>
         {
            _symbol,
            _symbolCurrentMonth
         } );
      }

      /// <summary>
      /// Called when there is an update on a order placed by this strategy
      /// </summary>
      /// <param name="order">Order object</param>
      /// <returns>Async Task</returns>
      public override async Task OnOrderUpdateAsync( Order order )
      {
         try
         {
            switch ( order.Status )
            {
            case OrderStatus.Cancelled:
               {
                  lock ( _state )
                     _state.Orders.Add( order );

                  _state.Bought = false;
                  _state.CurrentOrder = null;

                  // Log the cancel
                  var log = $"{order.OrderTimestamp}, Cancelled Order Id {order.OrderId}, Qty {order.CancelledQuantity} units of {order.Symbol.Ticker}, Asked Price {order.Price}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }

               break;

            case OrderStatus.Completed:

               lock ( _state )
                  _state.Orders.Add( order );

               if ( !_state.Short )
               {
                  if ( order.OrderDirection == OrderDirection.Buy )
                  {
                     _state.Bought = true;
                     _state.CurrentOrder = order;
                     _state.CurrentOrder.Short = _state.Short;

                     // Log the buy
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );
                  }
                  else
                  {
                     _state.Bought = false;
                     _state.CurrentOrder = null;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );
                  }
               }
               else
               {
                  if ( order.OrderDirection == OrderDirection.Sell )
                  {
                     _state.Bought = true;
                     _state.CurrentOrder = order;
                     _state.CurrentOrder.Short = _state.Short;

                     // Log the buy
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );
                  }
                  else
                  {
                     _state.Bought = false;
                     _state.CurrentOrder = null;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );
                  }
               }

               _state.ProcessingOrder = false;

               break;
            default:
               // Log the order status
               {
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Status {order.Status}, Message {order.StatusMessage}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }

               break;
            }

            _state.CurrentOrderId = string.Empty;

            var stats = GetStats( _state.CurrentTick );
            await SendAsync( "publish_stats", stats );

            foreach ( var kvp in stats )
            {
               var log = $"{kvp.Key}: {kvp.Value}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );
            }

            // Store our _state
            await SetDataAsync( "_state", _state );
         }
         catch ( Exception ex )
         {
            await LogAsync( LogLevel.Error, ex.ToString() );
            Console.WriteLine( ex );
         }
      }

      /// <summary>
      /// Called on every tick of the data
      /// </summary>
      /// <param name="tickData">TickData object</param>
      /// <returns>Async Task</returns>
      public override async Task OnTickAsync( TickData tickData )
      {
         try
         {
            var prevTick = _state.CurrentTick;

            if ( tickData.Symbol.Equals( _symbolCurrentMonth ) )
               _state.CurrentTick = tickData;
            else
               _state.IdxCurrentTick = tickData;

            if ( _state.IdxCurrentTick == null || _state.CurrentTick == null )
               return;

            if ( prevTick != null && prevTick.Timestamp.Month != tickData.Timestamp.Month )
            {
               _state.BreakInMonth = false;
               _state.MonthPL = 0;
            }

            if ( prevTick == null || ( !_state.DayChanged && tickData.Timestamp.Day > prevTick.Timestamp.Day && !_state.Bought ) )
            {
               _state.DayChanged = true;
               await _indicatorEvaluator.ClearCandlesAsync();
            }

            var (direction, strength) = await _indicatorEvaluator.TRENDAsync( 60 );

            if ( ( _state.LastTick == null ) || ( tickData.Timestamp - _state.LastTick.Timestamp ).TotalMinutes >= 1 )
            {
               await LogAsync( LogLevel.Debug, $"{tickData.Timestamp}, {tickData.LTP}, d {direction}, s {strength}, bim {_state.BreakInMonth}" );
               _state.LastTick = tickData;
            }

            if ( _state.BreakInMonth && ( !_state.Bought ) )
               return;

            if (
               ( _state.IdxCurrentTick.Timestamp.Hour >= 9 ) && ( _state.IdxCurrentTick.Timestamp.Hour <= 14 ) &&
               ( ( ( _state.CurrentTick.Timestamp.Hour >= 9 ) && ( _state.CurrentTick.Timestamp.Minute >= 30 ) ) || ( _state.CurrentTick.Timestamp.Hour >= 10 ) ) &&
               _state.DayChanged &&
               direction == 2 && strength >= 10 &&
               ( !_state.Bought ) && ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
            {
               await ShortOrderAsync( _state.CurrentTick, _state.CurrentTick.LTP );
            }
            else if (
               ( _state.IdxCurrentTick.Timestamp.Hour >= 9 ) && ( _state.IdxCurrentTick.Timestamp.Hour <= 14 ) &&
               ( ( ( _state.CurrentTick.Timestamp.Hour >= 9 ) && ( _state.CurrentTick.Timestamp.Minute >= 30 ) ) || ( _state.CurrentTick.Timestamp.Hour >= 10 ) ) &&
               _state.DayChanged &&
               direction == 1 && strength >= 10 &&
               ( !_state.Bought ) && ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
            {
               await LongOrderAsync( _state.CurrentTick, _state.CurrentTick.LTP );
            }
            else if ( _state.CurrentOrder != null )
            {
               if (
                     ( !_state.Short &&
                     (
                        _state.CurrentTick.LTP - _state.CurrentOrder.AveragePrice >= ( _state.OffsetOrder ? 20 : 10 ) ||
                        _state.CurrentOrder.AveragePrice - _state.CurrentTick.LTP >= 20 ||
                        _state.CurrentTick.Timestamp.Hour == 15
                     ) )
                     &&
                  ( _state.Bought ) &&
                  !_state.ProcessingOrder )
               {
                  if ( _state.OffsetOrder )
                     _state.OffsetOrder = false;

                  await LogAsync( LogLevel.Information, $"OAP {_state.CurrentOrder.AveragePrice}, LTP {_state.CurrentTick.LTP}" );

                  _state.ProcessingOrder = true;

                  // Place sell order
                  _state.CurrentOrderId = Guid.NewGuid().ToString();
                  var qty = _state.CurrentOrder.FilledQuantity;

                  await PlaceOrderAsync( new PlaceOrderRequest()
                  {
                     OrderType = OrderType.Market,
                     Price = _state.CurrentTick.LTP,
                     Quantity = qty,
                     Symbol = _symbolCurrentMonth,
                     Timestamp = _state.CurrentTick.Timestamp,
                     TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                     TriggerPrice = _state.CurrentTick.LTP,
                     OrderDirection = OrderDirection.Sell,
                     Tag = _state.CurrentOrderId,
                     SlippageType = SlippageType.TIME,
                     Slippage = 1000
                  } );

                  // Store our state
                  await SetDataAsync( "state", _state );

                  // Log the sell initiation
                  var log = $"{_state.CurrentTick.Timestamp}, Placed sell order for {qty} units of {_symbolCurrentMonth.Ticker} at price (approx) {_state.CurrentTick.LTP}, {_state.CurrentTick.Timestamp}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
               else if (
                        ( _state.Short &&
                        (
                           _state.CurrentOrder.AveragePrice - _state.CurrentTick.LTP >= ( _state.OffsetOrder ? 20 : 10 ) ||
                           _state.CurrentTick.LTP - _state.CurrentOrder.AveragePrice >= 20 ||
                           _state.CurrentTick.Timestamp.Hour == 15
                        ) )
                        &&
                     ( _state.Bought ) &&
                     !_state.ProcessingOrder )
               {
                  if ( _state.OffsetOrder )
                     _state.OffsetOrder = false;

                  await LogAsync( LogLevel.Information, $"OAP {_state.CurrentOrder.AveragePrice}, LTP {_state.CurrentTick.LTP}" );

                  _state.ProcessingOrder = true;

                  // Place sell order
                  _state.CurrentOrderId = Guid.NewGuid().ToString();
                  var qty = _state.CurrentOrder.FilledQuantity;

                  await PlaceOrderAsync( new PlaceOrderRequest()
                  {
                     OrderType = OrderType.Market,
                     Price = _state.CurrentTick.LTP,
                     Quantity = qty,
                     Symbol = _symbolCurrentMonth,
                     Timestamp = _state.CurrentTick.Timestamp,
                     TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                     TriggerPrice = _state.CurrentTick.LTP,
                     OrderDirection = OrderDirection.Buy,
                     Tag = _state.CurrentOrderId,
                     SlippageType = SlippageType.TIME,
                     Slippage = 1000
                  } );

                  // Store our state
                  await SetDataAsync( "state", _state );

                  // Log the sell initiation
                  var log = $"{_state.CurrentTick.Timestamp}, Placed buy (short) order for {qty} units of {_symbolCurrentMonth.Ticker} at price (approx) {_state.CurrentTick.LTP}, {_state.CurrentTick.Timestamp}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
            }

            _state.PrevTick = tickData;
         }
         catch ( Exception ex )
         {
            await LogAsync( LogLevel.Error, ex.ToString() );

            // DIAG::
            Console.WriteLine( ex );
         }
         finally
         {
            await SendProgressAsync( tickData );
         }
      }

      private async Task LongOrderAsync( TickData tickData, double price )
      {
         _state.DayChanged = false;
         _state.Short = false;

         // Place buy order
         _state.CurrentOrderId = Guid.NewGuid().ToString();
         var qty = Math.Floor( Capital / tickData.LTP ) * Leverage;

         _state.ProcessingOrder = true;

         var placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = price,
            Quantity = qty,
            Symbol = _symbolCurrentMonth,
            Timestamp = tickData.Timestamp,
            TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
            TriggerPrice = tickData.LTP,
            OrderDirection = OrderDirection.Buy,
            Tag = _state.CurrentOrderId,
            OrderProductType = OrderProductType.Intraday,
            SlippageType = SlippageType.TIME,
            Slippage = 1000
         };

         await PlaceOrderAsync( placeOrderRequest );

         // Store our _state
         await SetDataAsync( "_state", _state );

         // Log the buy initiation
         var log = $"{tickData.Timestamp}, Placed buy order for {qty} units of {_symbolCurrentMonth.Ticker} at price (approx) {( placeOrderRequest.OrderType == OrderType.Market ? tickData.LTP : placeOrderRequest.Price )}, {tickData.Timestamp}";
         await LogAsync( LogLevel.Information, log );

         // DIAG::
         Console.WriteLine( log );
      }

      private async Task ShortOrderAsync( TickData tickData, double price )
      {
         _state.DayChanged = false;
         _state.Short = true;

         // Place buy order
         _state.CurrentOrderId = Guid.NewGuid().ToString();
         var qty = Math.Floor( Capital / tickData.LTP ) * Leverage;

         _state.ProcessingOrder = true;

         var placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = price,
            Quantity = qty,
            Symbol = _symbolCurrentMonth,
            Timestamp = tickData.Timestamp,
            TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
            TriggerPrice = tickData.LTP,
            OrderDirection = OrderDirection.Sell,
            Tag = _state.CurrentOrderId,
            OrderProductType = OrderProductType.Intraday,
            SlippageType = SlippageType.TIME,
            Slippage = 1000
         };

         await PlaceOrderAsync( placeOrderRequest );

         // Store our _state
         await SetDataAsync( "_state", _state );

         // Log the buy initiation
         var log = $"{tickData.Timestamp}, Placed sell (short) order for {qty} units of {_symbolCurrentMonth.Ticker} at price (approx) {( placeOrderRequest.OrderType == OrderType.Market ? tickData.LTP : placeOrderRequest.Price )}, {tickData.Timestamp}";
         await LogAsync( LogLevel.Information, log );

         // DIAG::
         Console.WriteLine( log );
      }

      /// <summary>
      /// Called on any unsolicited errors from user's Quant Engine
      /// </summary>
      /// <param name="errorMsg">Error message</param>
      public override async Task OnErrorAsync( string errorMsg )
      {
         await base.OnErrorAsync( errorMsg );
      }

      /// <summary>
      /// Called on custom events passed on by users to the strategy
      /// </summary>
      /// <param name="eventData">Custom event data string</param>
      /// <returns>Async Task</returns>
      public override async Task<string> OnCustomEventAsync( string eventData )
      {
         await LogAsync( LogLevel.Information, eventData );
         return string.Empty;
      }

      /// <summary>
      /// Start trading
      /// </summary>
      /// <param name="tradingRequest">TradingRequest object</param>
      /// <returns></returns>
      public override async Task StartTradingAsync( TradingRequest tradingRequest )
      {
         await base.StartTradingAsync( tradingRequest );
      }

      /// <summary>
      /// Backtest this strategy
      /// </summary>
      /// <param name="backtestRequest">BacktestRequest object</param>
      /// <returns>Backtest id</returns>
      public override async Task<string> BacktestAsync( BacktestRequest backtestRequest )
      {
         // Run backtest
         return await base.BacktestAsync( backtestRequest );
      }

      public override Dictionary<string, object> GetStats( TickData tickData )
      {
         var statsMap = new Dictionary<string, object>();

         statsMap["Capital"] = Capital;
         statsMap["Order Count"] = _state.Orders.Count;

         var groupedOrders = from order in _state.Orders
                             group order by new { Month = order.OrderTimestamp.Value.Month, Year = order.OrderTimestamp.Value.Year } into d
                             select new { Date = $"{d.Key.Month:00}-{d.Key.Year:0000}", Orders = d.ToList() };

         double buyVal = 0;
         double sellVal = 0;
         double buyQty = 0;
         double sellQty = 0;

         foreach ( var orderGroup in groupedOrders )
         {
            double buyValMonth = 0;
            double sellValMonth = 0;
            double buyQtyMonth = 0;
            double sellQtyMonth = 0;

            foreach ( var order in orderGroup.Orders )
            {
               if ( ( order.Status == OrderStatus.Completed ) && ( order.OrderDirection == OrderDirection.Buy ) && order.Symbol.IsMatch( tickData ) )
               {
                  buyVal += order.FilledQuantity * order.AveragePrice;
                  buyQty += order.FilledQuantity;

                  buyValMonth += order.FilledQuantity * order.AveragePrice;
                  buyQtyMonth += order.FilledQuantity;
               }

               if ( ( order.Status == OrderStatus.Completed ) && ( order.OrderDirection == OrderDirection.Sell ) && order.Symbol.IsMatch( tickData ) )
               {
                  sellVal += order.FilledQuantity * order.AveragePrice;
                  sellQty += order.FilledQuantity;

                  sellValMonth += order.FilledQuantity * order.AveragePrice;
                  sellQtyMonth += order.FilledQuantity;
               }
            }

            if ( _state.Bought )
            {
               if ( !_state.Short )
               {
                  if ( sellQty < buyQty )
                     sellVal += ( buyQty - sellQty ) * tickData.LTP;

                  if ( sellQtyMonth < buyQtyMonth )
                     sellValMonth += ( buyQtyMonth - sellQtyMonth ) * tickData.LTP;
               }
               else
               {
                  if ( buyQty < sellQty )
                     buyVal += ( sellQty - buyQty ) * tickData.LTP;

                  if ( buyQtyMonth < sellQtyMonth )
                     buyValMonth += ( sellQtyMonth - buyQtyMonth ) * tickData.LTP;
               }
            }

            double plMonth = sellValMonth - buyValMonth;
            statsMap[$"PL-{orderGroup.Date}"] = plMonth;
         }

         double pl = sellVal - buyVal;
         statsMap["PL"] = pl;
         statsMap["Portfolio Value"] = Capital + pl;

         return statsMap;
      }
   }
}