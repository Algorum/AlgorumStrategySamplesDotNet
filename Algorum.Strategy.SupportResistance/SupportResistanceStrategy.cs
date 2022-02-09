using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Algorum.Strategy.SupportResistance
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   public class SupportResistanceStrategy : QuantEngineClient
   {
      private class State
      {
         public bool Bought;
         public TickData LastTick;
         public TickData CurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public bool TouchedSupport;
      }

      public const double Capital = 100000;
      private const double Leverage = 1; // 1x Leverage on margin by Brokerage

      private Symbol _symbol;
      private IIndicatorEvaluator _indicatorEvaluator;
      private State _state;

      /// <summary>
      /// Helps create SupportResistanceStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <returns>Instance of SupportResistanceStrategy class</returns>
      public static async Task<SupportResistanceStrategy> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new SupportResistanceStrategy( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private SupportResistanceStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
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
            _state.TouchedSupport = false;
         }

         // Create our stock symbol object
         // For India users
         _symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "TATAMOTORS" };

         // For USA users
         //_symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "AAPL" };

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

         // Get the support and resistance values
         var (supportValue, supportScore, resistanceValue, resistanceScore) = await _indicatorEvaluator.SUPPORTRESISTANCEAsync( 60, 0, 10 );

         if ( ( _state.LastTick == null ) || ( tickData.Timestamp - _state.LastTick.Timestamp ).TotalMinutes >= 1 )
         {
            await LogAsync( LogLevel.Debug, $"{tickData.Timestamp}, {tickData.LTP}, sv {supportValue}, ss {supportScore}, " +
               $"rv {resistanceValue}, rs {resistanceScore}" );
            _state.LastTick = tickData;
         }

         // We wait until the stock price touches below the support value
         if ( !_state.TouchedSupport && supportScore > 0 && tickData.LTP <= supportValue && !_state.Bought )
            _state.TouchedSupport = true;

         // We BUY the stock when the stock price touches below the support value and then moves above the
         // support value, and is below the half the distance to the resistance value
         if ( supportScore > 0 && tickData.LTP > supportValue && resistanceScore > 0 &&
            tickData.LTP < resistanceValue - ( ( resistanceValue - tickData.LTP ) / 2 ) && _state.TouchedSupport &&
            ( !_state.Bought ) && ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
         {
            _state.TouchedSupport = false;

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
               TriggerPrice = tickData.LTP,
               OrderDirection = OrderDirection.Buy,
               SlippageType = SlippageType.TIME,
               Slippage = 1000,
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
                  ( _state.CurrentOrder.AveragePrice - tickData.LTP >= _state.CurrentOrder.AveragePrice * 0.25 / 100 ) )
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
                  SlippageType = SlippageType.TIME,
                  Slippage = 1000,
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

      private async Task<StrategyRunSummary> GetStrategyRunSummaryAsync()
      {
         if ( _symbol == null )
            return null;

         var symbolState = _state;

         var summary = await GetStrategyRunSummaryAsync( Capital, ( symbolState.CurrentTick != null ? new List<KeyValuePair<Symbol, TickData>>()
            {
               new KeyValuePair<Symbol, TickData>(_symbol, symbolState.CurrentTick)
            } : null ) );

         return summary;
      }

      public async override Task<Dictionary<string, object>> GetStatsAsync( TickData tickData )
      {
         if ( _symbol == null )
            return new Dictionary<string, object>();

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
               stats = summary.SymbolStats.FirstOrDefault( obj => obj.Key.Equals( _symbol ) );

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