using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Algorum.Strategy.TrendReversal
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   public class TrendReversalStrategy : QuantEngineClient
   {
      private class State
      {
         public bool Bought;
         public TickData LastTick;
         public TickData CurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public CrossBelow CrossBelowObj;
         public CrossBelow CrossBelowShortObj;
         public bool Short;
      }

      public const double Capital = 30000;
      private const double Leverage = 2; // 1x Leverage on margin by Brokerage
      private const int DIRECTION_UP = 1;
      private const int DIRECTION_DOWN = 2;

      private Symbol _symbol;
      private IIndicatorEvaluator _indicatorEvaluator;
      private State _state;

      /// <summary>
      /// Helps create TrendReversalStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <returns>Instance of TrendReversalStrategy class</returns>
      public static async Task<TrendReversalStrategy> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new TrendReversalStrategy( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private TrendReversalStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
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
            _state.CrossBelowObj = new CrossBelow();
            _state.CrossBelowShortObj = new CrossBelow();
         }

         // Create our stock symbol object
         // For India users
         _symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "TATAMOTORS" };

         // For USA users
         //_symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "SPY" };

         // Create the technical indicator evaluator that can work with minute candles of the stock
         // This will auto sync with the new tick data that would be coming in for this symbol
         _indicatorEvaluator = await CreateIndicatorEvaluatorAsync( new CreateIndicatorRequest()
         {
            Symbol = _symbol,
            CandlePeriod = CandlePeriod.Minute,
            PeriodSpan = 1
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

               if ( !_state.Short )
               {
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
                     _state.Short = false;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
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

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Sold (Short) {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );
                  }
                  else
                  {
                     _state.Bought = false;
                     _state.CurrentOrder = null;
                     _state.Short = false;

                     // Log the buy
                     var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Bought (Short) {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );
                  }
               }

               _state.CurrentOrderId = string.Empty;

               var stats = await GetStatsAsync( _state.CurrentTick );
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

         // Get the trend and strength
         var (direction, strength) = await _indicatorEvaluator.TRENDAsync( 60 );
         var rsi = await _indicatorEvaluator.RSIAsync( 14 );

         if ( ( _state.LastTick == null ) || ( tickData.Timestamp - _state.LastTick.Timestamp ).TotalMinutes >= 1 )
         {
            var log = $"{tickData.Timestamp}, {tickData.Symbol}, {tickData.LTP}, ld {direction}, ls {strength}, rsi {rsi}";
            await LogAsync( LogLevel.Debug, log );
            _state.LastTick = tickData;

            // DIAG::
            Console.WriteLine( log );
         }

         // We BUY the stock when the direction was strongly DOWN and started moving UP and RSI is over sold
         if (
            (
               ( direction == DIRECTION_DOWN && _state.CrossBelowObj.Evaluate( strength, 9 ) && rsi <= 40 ) ||
               ( direction == DIRECTION_UP && _state.CrossBelowShortObj.Evaluate( strength, 9 ) && rsi >= 60 )
            ) &&
            ( !_state.Bought ) && ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
         {
            // Place buy order
            _state.CurrentOrderId = Guid.NewGuid().ToString();
            var qty = Math.Floor( Capital / tickData.LTP ) * Leverage;

            if ( direction == DIRECTION_DOWN )
            {
               await PlaceOrderAsync( new PlaceOrderRequest()
               {
                  OrderType = OrderType.Market,
                  Price = tickData.LTP,
                  Quantity = qty,
                  Symbol = tickData.Symbol,
                  Timestamp = tickData.Timestamp,
                  TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                  TriggerPrice = tickData.LTP,
                  OrderDirection = OrderDirection.Buy,
                  SlippageType = SlippageType.TIME,
                  Slippage = 1000,
                  Tag = _state.CurrentOrderId
               } );

               _state.Short = false;

               // Store our state
               await SetDataAsync( "state", _state );

               // Log the buy initiation
               var log = $"{tickData.Timestamp}, Placed buy order for {qty} units of {tickData.Symbol.Ticker} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );
            }
            else if ( direction == DIRECTION_UP )
            {
               await PlaceOrderAsync( new PlaceOrderRequest()
               {
                  OrderType = OrderType.Market,
                  Price = tickData.LTP,
                  Quantity = qty,
                  Symbol = tickData.Symbol,
                  Timestamp = tickData.Timestamp,
                  TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                  TriggerPrice = tickData.LTP,
                  OrderDirection = OrderDirection.Sell,
                  SlippageType = SlippageType.TIME,
                  Slippage = 1000,
                  Tag = _state.CurrentOrderId
               } );

               _state.Short = true;

               // Store our state
               await SetDataAsync( "state", _state );

               // Log the buy initiation
               var log = $"{tickData.Timestamp}, Placed sell (short) order for {qty} units of {tickData.Symbol.Ticker} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );
            }
         }
         else if ( _state.CurrentOrder != null )
         {
            if ( !_state.Short )
            {
               if ( (
                     ( tickData.LTP - _state.CurrentOrder.AveragePrice >= _state.CurrentOrder.AveragePrice * 0.50 / 100 ) ||
                     ( _state.CurrentOrder.AveragePrice - tickData.LTP >= _state.CurrentOrder.AveragePrice * 1.0 / 100 ) )
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
                     Symbol = tickData.Symbol,
                     Timestamp = tickData.Timestamp,
                     TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                     TriggerPrice = tickData.LTP,
                     OrderDirection = OrderDirection.Sell,
                     SlippageType = SlippageType.TIME,
                     Slippage = 1000,
                     Tag = _state.CurrentOrderId
                  } );

                  _state.CurrentOrder = null;

                  // Store our state
                  await SetDataAsync( "state", _state );

                  // Log the sell initiation
                  var log = $"{tickData.Timestamp}, Placed sell order for {qty} units of {tickData.Symbol.Ticker} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
            }
            else
            {
               if ( (
                     ( _state.CurrentOrder.AveragePrice - tickData.LTP >= _state.CurrentOrder.AveragePrice * 0.25 / 100 ) ||
                     ( tickData.LTP - _state.CurrentOrder.AveragePrice >= _state.CurrentOrder.AveragePrice * 0.50 / 100 ) )
                     &&
                  ( _state.Bought ) )
               {
                  await LogAsync( LogLevel.Information, $"OAP {_state.CurrentOrder.AveragePrice}, LTP {tickData.LTP}" );

                  // Place buy order
                  _state.CurrentOrderId = Guid.NewGuid().ToString();
                  var qty = _state.CurrentOrder.FilledQuantity;

                  await PlaceOrderAsync( new PlaceOrderRequest()
                  {
                     OrderType = OrderType.Market,
                     Price = tickData.LTP,
                     Quantity = qty,
                     Symbol = tickData.Symbol,
                     Timestamp = tickData.Timestamp,
                     TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                     TriggerPrice = tickData.LTP,
                     OrderDirection = OrderDirection.Buy,
                     SlippageType = SlippageType.TIME,
                     Slippage = 1000,
                     Tag = _state.CurrentOrderId
                  } );

                  _state.CurrentOrder = null;

                  // Store our state
                  await SetDataAsync( "state", _state );

                  // Log the sell initiation
                  var log = $"{tickData.Timestamp}, Placed buy (short) order for {qty} units of {tickData.Symbol.Ticker} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
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
         await _indicatorEvaluator.PreloadCandlesAsync( 2, DateTime.UtcNow, tradingRequest.ApiKey, tradingRequest.ApiSecretKey );

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
         await _indicatorEvaluator.PreloadCandlesAsync( 2, backtestRequest.StartDate.AddDays( 1 ), backtestRequest.ApiKey, backtestRequest.ApiSecretKey );

         // Run backtest
         return await base.BacktestAsync( backtestRequest );
      }

      private async Task<StrategyRunSummary> GetStrategyRunSummaryAsync()
      {
         var symbolState = _state;

         var summary = await GetStrategyRunSummaryAsync( Capital, ( symbolState.CurrentTick != null ? new List<KeyValuePair<Symbol, TickData>>()
            {
               new KeyValuePair<Symbol, TickData>(symbolState.CurrentTick.Symbol, symbolState.CurrentTick)
            } : null ), StatsType.Individual, 0 );

         return summary;
      }

      public async override Task<Dictionary<string, object>> GetStatsAsync( TickData tickData )
      {
         try
         {
            var statsMap = new Dictionary<string, object>();
            var symbolState = _state;

            var summary = await GetStrategyRunSummaryAsync();

            statsMap.Add( "Total Capital", summary.Capital );
            statsMap.Add( "Total PL", summary.PL );
            statsMap.Add( "Total Max Draw Down", summary.MaxDrawdown );

            KeyValuePair<Symbol, Dictionary<string, object>> stats = default( KeyValuePair<Symbol, Dictionary<string, object>> );

            if ( summary != null && summary.SymbolStats != null )
            {
               stats = summary.SymbolStats.FirstOrDefault( obj => obj.Key.Equals( tickData.Symbol ) );

               if ( stats.Key != null && stats.Value != null )
               {
                  foreach ( var kvp in stats.Value )
                  {
                     statsMap.Add( $"{stats.Key}-{kvp.Key}", kvp.Value );
                  }
               }
            }

            return statsMap;
         }
         catch ( Exception ex )
         {
            Console.WriteLine( ex );
            return new Dictionary<string, object>();
         }
      }
   }
}