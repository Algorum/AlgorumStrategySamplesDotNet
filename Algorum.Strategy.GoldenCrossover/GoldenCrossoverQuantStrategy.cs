using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Algorum.Strategy.GoldenCrossover
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   public class GoldenCrossoverQuantStrategy : QuantEngineClient
   {
      private class State
      {
         public bool Bought;
         public TickData LastTick;
         public TickData CurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public CrossAbove CrossAboveObj;
      }

      private const double Capital = 100000;
      private const double Leverage = 1; // 1x Leverage on margin by Brokerage

      private Symbol _symbol;
      private IIndicatorEvaluator _indicatorEvaluator;
      private State _state;

      /// <summary>
      /// Helps create GoldenCrossoverQuantStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <returns>Instance of GoldenCrossoverQuantStrategy class</returns>
      public static async Task<GoldenCrossoverQuantStrategy> GetInstanceAsync( string url, string apiKey, StrategyLaunchMode launchMode, string sid )
      {
         var strategy = new GoldenCrossoverQuantStrategy( url, apiKey, launchMode, sid );
         await strategy.InitializeAsync();
         return strategy;
      }

      private GoldenCrossoverQuantStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid )
         : base( url, apiKey, launchMode, sid )
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
            _state.CrossAboveObj = new CrossAbove();
         }

         // Create our stock symbol object
         _symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "AAPL" };

         // Create the technical indicator evaluator that can work with minute candles of the stock
         // This will auto sync with the new tick data that would be coming in for this symbol
         _indicatorEvaluator = await CreateIndicatorEvaluatorAsync( new CreateIndicatorRequest()
         {
            Symbol = _symbol,
            CandlePeriod = CandlePeriod.Minute,
            PeriodSpan = 5
         } );

         // Subscribe to the symbols we want (one second tick data)
         await SubscribeSymbolsAsync( new List<Symbol>
         {
            _symbol
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
         if ( string.Compare( order.Tag, _state.CurrentOrderId ) == 0 )
         {
            switch ( order.Status )
            {
            case OrderStatus.Completed:

               lock ( _state )
                  _state.Orders.Add( order );

               if ( order.OrderDirection == OrderDirection.Buy )
               {
                  _state.Bought = true;
                  _state.CurrentOrder = order;

                  // Log the buy
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
               else
               {
                  _state.Bought = false;
                  _state.CurrentOrder = null;

                  // Log the sell
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }

               _state.CurrentOrderId = string.Empty;

               var stats = GetStats( _state.CurrentTick );
               await SendAsync( "publish_stats", stats );

               foreach ( var kvp in stats )
                  Console.WriteLine( $"{kvp.Key}: {kvp.Value}" );

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
      /// Called on every tick of the data
      /// </summary>
      /// <param name="tickData">TickData object</param>
      /// <returns>Async Task</returns>
      public override async Task OnTickAsync( TickData tickData )
      {
         _state.CurrentTick = tickData;

         // Get the EMA values
         var ema50 = await _indicatorEvaluator.EMAAsync( 50 );
         var ema200 = await _indicatorEvaluator.EMAAsync( 200 );

         if ( ( _state.LastTick == null ) || ( tickData.Timestamp - _state.LastTick.Timestamp ).TotalMinutes >= 1 )
         {
            await LogAsync( LogLevel.Debug, $"{tickData.Timestamp}, {tickData.LTP}, ema50 {ema50}, ema200 {ema200}" );
            _state.LastTick = tickData;
         }

         if ( ema50 > 0 && ema200 > 0 && _state.CrossAboveObj.Evaluate( ema50, ema200 ) && ( !_state.Bought ) &&
            ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
         {
            // Place buy order
            _state.CurrentOrderId = Guid.NewGuid().ToString();
            var qty = Math.Floor( Capital / tickData.LTP ) * Leverage;

            await PlaceOrderAsync( new PlaceOrderRequest()
            {
               OrderType = OrderType.Market,
               Price = tickData.LTP,
               Quantity = qty,
               Symbol = _symbol,
               Timestamp = tickData.Timestamp,
               TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
               TriggerPrice = tickData.LTP + 0.1,
               OrderDirection = OrderDirection.Buy,
               Tag = _state.CurrentOrderId
            } );

            // Store our state
            await SetDataAsync( "state", _state );

            // Log the buy initiation
            var log = $"{tickData.Timestamp}, Placed buy order for {qty} units of {_symbol.Ticker} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
            await LogAsync( LogLevel.Information, log );

            // DIAG::
            Console.WriteLine( log );
         }
         else if ( _state.CurrentOrder != null )
         {
            if ( (
                  ( tickData.LTP - _state.CurrentOrder.AveragePrice >= _state.CurrentOrder.AveragePrice * 0.25 / 100 ) ||
                  ( _state.CurrentOrder.AveragePrice - tickData.LTP >= _state.CurrentOrder.AveragePrice * 0.50 / 100 ) )
                  &&
               ( _state.Bought ) )
            {
               await LogAsync( LogLevel.Information, $"OAP {_state.CurrentOrder.AveragePrice}, LTP {tickData.LTP}" );

               // Place sell order
               _state.CurrentOrderId = Guid.NewGuid().ToString();
               var qty = _state.CurrentOrder.FilledQuantity;

               await PlaceOrderAsync( new PlaceOrderRequest()
               {
                  OrderType = OrderType.Market,
                  Price = tickData.LTP,
                  Quantity = qty,
                  Symbol = _symbol,
                  Timestamp = tickData.Timestamp,
                  TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                  TriggerPrice = tickData.LTP,
                  OrderDirection = OrderDirection.Sell,
                  Tag = _state.CurrentOrderId
               } );

               _state.CurrentOrder = null;

               // Store our state
               await SetDataAsync( "state", _state );

               // Log the sell initiation
               var log = $"{tickData.Timestamp}, Placed sell order for {qty} units of {_symbol.Ticker} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );
            }
         }

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
      /// Start trading
      /// </summary>
      /// <param name="tradingRequest">TradingRequest object</param>
      /// <returns></returns>
      public override async Task StartTradingAsync( TradingRequest tradingRequest )
      {
         // Preload candles
         await _indicatorEvaluator.PreloadCandlesAsync( 210, DateTime.UtcNow, tradingRequest.ApiKey, tradingRequest.ApiSecretKey );

         await base.StartTradingAsync( tradingRequest );
      }

      /// <summary>
      /// Backtest this strategy
      /// </summary>
      /// <param name="backtestRequest">BacktestRequest object</param>
      /// <returns>Backtest id</returns>
      public override async Task<string> BacktestAsync( BacktestRequest backtestRequest )
      {
         // Preload candles
         await _indicatorEvaluator.PreloadCandlesAsync( 210, backtestRequest.StartDate.AddDays( 1 ), backtestRequest.ApiKey, backtestRequest.ApiSecretKey );

         // Run backtest
         return await base.BacktestAsync( backtestRequest );
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
            if ( ( order.Status == OrderStatus.Completed ) && ( order.OrderDirection == OrderDirection.Buy ) && order.Symbol.IsMatch( tickData ) )
            {
               buyVal += order.FilledQuantity * order.AveragePrice;
               buyQty += order.FilledQuantity;
            }

            if ( ( order.Status == OrderStatus.Completed ) && ( order.OrderDirection == OrderDirection.Sell ) && order.Symbol.IsMatch( tickData ) )
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