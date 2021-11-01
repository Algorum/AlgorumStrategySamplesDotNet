using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Aditya.Strategy.CashFuturesArbitrage
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   public class CashFuturesArbitrageQuantStrategy : QuantEngineClient
   {
      private class State
      {
         public volatile bool Bought;
         public volatile TickData LastTick;
         public volatile TickData CurrentStockTick;
         public volatile TickData CurrentFuturesTick;
         public List<Order> Orders;
         public volatile string CurrentFuturesOrderId;
         public volatile string CurrentStockOrderId;
         public volatile Order CurrentFuturesOrder;
         public volatile Order CurrentStockOrder;
         public volatile bool Processing;
      }

      private const double Capital = 2500000;
      private const double Leverage = 1; // 1x Leverage on margin by Brokerage

      private Symbol _symbolCash;
      private Symbol _symbolFutures;
      private State _state;

      /// <summary>
      /// Helps create CashFuturesArbitrageQuantStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <returns>Instance of CashFuturesArbitrageQuantStrategy class</returns>
      public static async Task<CashFuturesArbitrageQuantStrategy> GetInstanceAsync( string url, string apiKey, StrategyLaunchMode launchMode, string sid )
      {
         var strategy = new CashFuturesArbitrageQuantStrategy( url, apiKey, launchMode, sid );
         await strategy.InitializeAsync();
         return strategy;
      }

      private CashFuturesArbitrageQuantStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid )
         : base( url, apiKey, launchMode, sid )
      {
         // No-Op
      }

      private async Task InitializeAsync()
      {
         // Load any saved state
         _state = await GetDataAsync<State>( "state" );

         if ( _state == null )
         {
            _state = new State();
            _state.Orders = new List<Order>();
         }

         // Create our stock symbol object
         _symbolCash = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "TATAMOTORS" };
         _symbolFutures = new Symbol() { SymbolType = SymbolType.FuturesStock, Ticker = "TATAMOTORS" };

         // Subscribe to the symbols we want (one second tick data)
         await SubscribeSymbolsAsync( new List<Symbol>
         {
            _symbolCash,
            _symbolFutures
         } );
      }

      /// <summary>
      /// Called when there is an update on a order placed by this strategy
      /// </summary>
      /// <param name="order">Order object</param>
      /// <returns>Async Task</returns>
      public override async Task OnOrderUpdateAsync( Order order )
      {
         // Process only orders initiated by this strategy
         if ( string.Compare( order.Tag, _state.CurrentStockOrderId ) == 0 )
         {
            switch ( order.Status )
            {
            case OrderStatus.Completed:

               lock ( _state )
                  _state.Orders.Add( order );

               if ( order.OrderDirection == OrderDirection.Buy )
               {
                  _state.CurrentStockOrder = order;

                  // Log the buy
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );

                  if ( _state.CurrentFuturesOrder != null )
                  {
                     _state.Bought = true;

                     _state.CurrentStockOrderId = string.Empty;
                     _state.CurrentFuturesOrderId = string.Empty;

                     var stats = GetStats( _state.CurrentStockTick );
                     await SendAsync( "publish_stats", stats );

                     foreach ( var kvp in stats )
                        Console.WriteLine( $"{kvp.Key}: {kvp.Value}" );
                  }
               }
               else
               {
                  _state.CurrentStockOrder = null;

                  // Log the sell
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );

                  if ( _state.CurrentFuturesOrder == null )
                  {
                     _state.Bought = false;

                     _state.CurrentStockOrderId = string.Empty;
                     _state.CurrentFuturesOrderId = string.Empty;

                     var stats = GetStats( _state.CurrentStockTick );
                     await SendAsync( "publish_stats", stats );

                     foreach ( var kvp in stats )
                        Console.WriteLine( $"{kvp.Key}: {kvp.Value}" );
                  }
               }

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

            // Store our state
            await SetDataAsync( "state", _state );
         }
         else if ( string.Compare( order.Tag, _state.CurrentFuturesOrderId ) == 0 )
         {
            switch ( order.Status )
            {
            case OrderStatus.Completed:

               lock ( _state )
                  _state.Orders.Add( order );

               if ( order.OrderDirection == OrderDirection.Sell )
               {
                  _state.CurrentFuturesOrder = order;

                  // Log the sell
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );

                  if ( _state.CurrentStockOrder != null )
                  {
                     _state.Bought = true;

                     _state.CurrentStockOrderId = string.Empty;
                     _state.CurrentFuturesOrderId = string.Empty;

                     var stats = GetStats( _state.CurrentFuturesTick );
                     await SendAsync( "publish_stats", stats );

                     foreach ( var kvp in stats )
                        Console.WriteLine( $"{kvp.Key}: {kvp.Value}" );
                  }
               }
               else
               {
                  _state.CurrentFuturesOrder = null;

                  // Log the sell
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );

                  if ( _state.CurrentStockOrder == null )
                  {
                     _state.Bought = false;

                     _state.CurrentStockOrderId = string.Empty;
                     _state.CurrentFuturesOrderId = string.Empty;

                     var stats = GetStats( _state.CurrentFuturesTick );
                     await SendAsync( "publish_stats", stats );

                     foreach ( var kvp in stats )
                        Console.WriteLine( $"{kvp.Key}: {kvp.Value}" );
                  }
               }

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

            // Store our state
            await SetDataAsync( "state", _state );
         }

      }

      /// <summary>
      /// Called on every tick of the data (minimum 1 second resolution)
      /// </summary>
      /// <param name="tickData">TickData object</param>
      /// <returns>Async Task</returns>
      public override async Task OnTickAsync( TickData tickData )
      {
         if ( tickData.Symbol.SymbolType == SymbolType.Stock )
            _state.CurrentStockTick = tickData;
         else
            _state.CurrentFuturesTick = tickData;

         if ( ( _state.LastTick == null ) || ( tickData.Timestamp - _state.LastTick.Timestamp ).TotalMinutes >= 1 )
         {
            await LogAsync( LogLevel.Debug, $"{tickData.Timestamp}, {tickData.LTP}" );
            _state.LastTick = tickData;
         }

         if (
            ( _state.CurrentFuturesTick != null && _state.CurrentStockTick != null ) &&
            ( _state.CurrentFuturesTick.LTP - _state.CurrentStockTick.LTP > 1 ) &&
            ( !_state.Bought ) && ( string.IsNullOrWhiteSpace( _state.CurrentFuturesOrderId ) ) &&
            ( string.IsNullOrWhiteSpace( _state.CurrentStockOrderId ) ) && !_state.Processing )
         {
            _state.Processing = true;

            // Place buy order for the stock
            _state.CurrentStockOrderId = Guid.NewGuid().ToString();
            var qty = Math.Floor( Capital / _state.CurrentStockTick.LTP ) * Leverage;

            await PlaceOrderAsync( new PlaceOrderRequest()
            {
               OrderType = OrderType.Market,
               Price = _state.CurrentStockTick.LTP,
               Quantity = qty,
               Symbol = _symbolCash,
               Timestamp = _state.CurrentStockTick.Timestamp,
               TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
               TriggerPrice = _state.CurrentStockTick.LTP,
               OrderDirection = OrderDirection.Buy,
               Tag = _state.CurrentStockOrderId
            } );

            // Store our state
            await SetDataAsync( "state", _state );

            // Log the buy initiation
            var log = $"{_state.CurrentStockTick.Timestamp}, Placed buy order for {qty} units of {_symbolCash.Ticker} at price (approx) {_state.CurrentStockTick.LTP}, {_state.CurrentStockTick.Timestamp}";
            await LogAsync( LogLevel.Information, log );

            // DIAG::
            Console.WriteLine( log );

            // Place sell order for the future
            _state.CurrentFuturesOrderId = Guid.NewGuid().ToString();
            qty = Math.Floor( Capital / _state.CurrentFuturesTick.LTP ) * Leverage;

            await PlaceOrderAsync( new PlaceOrderRequest()
            {
               OrderType = OrderType.Market,
               Price = _state.CurrentFuturesTick.LTP,
               Quantity = qty,
               Symbol = _symbolFutures,
               Timestamp = _state.CurrentFuturesTick.Timestamp,
               TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
               TriggerPrice = _state.CurrentFuturesTick.LTP,
               OrderDirection = OrderDirection.Sell,
               Tag = _state.CurrentFuturesOrderId
            } );

            // Store our state
            await SetDataAsync( "state", _state );

            // Log the buy initiation
            log = $"{_state.CurrentFuturesTick.Timestamp}, Placed sell order for {qty} units of {_symbolFutures.Ticker} at price (approx) {_state.CurrentFuturesTick.LTP}, {_state.CurrentFuturesTick.Timestamp}";
            await LogAsync( LogLevel.Information, log );

            // DIAG::
            Console.WriteLine( log );

            _state.Processing = false;
         }
         else if ( ( _state.Bought ) && ( _state.CurrentFuturesOrder != null ) && ( _state.CurrentStockOrder != null ) && !_state.Processing )
         {
            if ( _state.CurrentFuturesTick.LTP - _state.CurrentStockTick.LTP <=
               ( _state.CurrentFuturesOrder.AveragePrice - _state.CurrentStockOrder.AveragePrice ) - 1 )
            {
               _state.Processing = true;

               // Place sell order for stock
               _state.CurrentStockOrderId = Guid.NewGuid().ToString();
               var qty = _state.CurrentStockOrder.FilledQuantity;

               await PlaceOrderAsync( new PlaceOrderRequest()
               {
                  OrderType = OrderType.Market,
                  Price = _state.CurrentStockTick.LTP,
                  Quantity = qty,
                  Symbol = _symbolCash,
                  Timestamp = _state.CurrentStockTick.Timestamp,
                  TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                  TriggerPrice = _state.CurrentStockTick.LTP,
                  OrderDirection = OrderDirection.Sell,
                  Tag = _state.CurrentStockOrderId
               } );

               // Store our state
               await SetDataAsync( "state", _state );

               // Log the sell initiation
               var log = $"{_state.CurrentStockTick.Timestamp}, Placed sell order for {qty} units of {_symbolCash.Ticker} at price (approx) {_state.CurrentStockTick.LTP}, {_state.CurrentStockTick.Timestamp}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );

               // Place buy order for future
               _state.CurrentFuturesOrderId = Guid.NewGuid().ToString();
               qty = _state.CurrentFuturesOrder.FilledQuantity;

               await PlaceOrderAsync( new PlaceOrderRequest()
               {
                  OrderType = OrderType.Market,
                  Price = _state.CurrentFuturesTick.LTP,
                  Quantity = qty,
                  Symbol = _symbolFutures,
                  Timestamp = _state.CurrentFuturesTick.Timestamp,
                  TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                  TriggerPrice = _state.CurrentFuturesTick.LTP,
                  OrderDirection = OrderDirection.Buy,
                  Tag = _state.CurrentFuturesOrderId
               } );

               // Store our state
               await SetDataAsync( "state", _state );

               // Log the sell initiation
               log = $"{_state.CurrentFuturesTick.Timestamp}, Placed buy order for {qty} units of {_symbolFutures.Ticker} at price (approx) {_state.CurrentFuturesTick.LTP}, {_state.CurrentFuturesTick.Timestamp}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );

               _state.Processing = false;
            }
         }

         if ( LaunchMode == StrategyLaunchMode.Backtesting )
            await SendProgressAsync( tickData );
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
      /// Backtest this strategy
      /// </summary>
      /// <param name="startDate">Start date of the candles</param>
      /// <param name="endDate">End date of the candles</param>
      /// <returns>Backtest id</returns>
      public override async Task<string> BacktestAsync( DateTime startDate, DateTime endDate, string bkAPiKey, string bkApiSecretKey, int samplingTimeInSeconds )
      {
         // Run backtest
         return await base.BacktestAsync( startDate, endDate, bkAPiKey, bkApiSecretKey, samplingTimeInSeconds );
      }

      public override Dictionary<string, object> GetStats( TickData tickData )
      {
         var statsMap = new Dictionary<string, object>();

         statsMap["Capital"] = Capital;
         statsMap["Order Count"] = _state.Orders.Count;

         double buyVal = 0;
         double sellVal = 0;
         double buyQty = 0;
         double sellQty = 0;

         foreach ( var order in _state.Orders )
         {
            if ( ( order.Status == OrderStatus.Completed ) &&
                     ( ( order.Symbol.SymbolType == SymbolType.Stock ) && ( order.OrderDirection == OrderDirection.Buy ) )
                     && order.Symbol.IsMatch( tickData ) )
            {
               buyVal += order.FilledQuantity * order.AveragePrice;
               buyQty += order.FilledQuantity;
            }

            if ( ( order.Status == OrderStatus.Completed ) &&
                     ( ( order.Symbol.SymbolType == SymbolType.FuturesStock ) && ( order.OrderDirection == OrderDirection.Sell ) )
                     && order.Symbol.IsMatch( tickData ) )
            {
               sellVal += order.FilledQuantity * order.AveragePrice;
               sellQty += order.FilledQuantity;
            }

            if ( ( order.Status == OrderStatus.Completed ) &&
                     ( ( order.Symbol.SymbolType == SymbolType.FuturesStock ) && ( order.OrderDirection == OrderDirection.Buy ) )
                     && order.Symbol.IsMatch( tickData ) )
            {
               buyVal += order.FilledQuantity * order.AveragePrice;
               buyQty += order.FilledQuantity;
            }

            if ( ( order.Status == OrderStatus.Completed ) &&
                     ( ( order.Symbol.SymbolType == SymbolType.Stock ) && ( order.OrderDirection == OrderDirection.Sell ) )
                     && order.Symbol.IsMatch( tickData ) )
            {
               sellVal += order.FilledQuantity * order.AveragePrice;
               sellQty += order.FilledQuantity;
            }
         }

         if ( sellQty < buyQty )
            sellVal += ( buyQty - sellQty ) * tickData.LTP;

         double pl = sellVal - buyVal;
         statsMap["PL"] = pl;
         statsMap["Portfolio Value"] = Capital + pl;

         return statsMap;
      }

   }
}