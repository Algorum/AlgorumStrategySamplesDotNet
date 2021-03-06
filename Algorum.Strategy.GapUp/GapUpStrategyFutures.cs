using System;
using System.Collections.Generic;
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
   public class GapUpStrategyFutures : QuantEngineClient
   {
      private class State
      {
         public bool Bought;
         public TickData LastTick;
         public TickData CurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public bool DayChanged;
         public TickData IndexCurrentTick;
      }

      public const double Capital = 100000;
      private const double Leverage = 1; // 1x Leverage on margin by Brokerage

      private Symbol _symbolIndex;
      private Symbol _symbolCurrentMonthFutures;
      private IIndicatorEvaluator _indicatorEvaluator;
      private State _state;
      private List<DateTime> _holidays;

      /// <summary>
      /// Helps create strategy class object class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <param name="userId">User unique id</param>
      /// <returns>Instance of GapUpStrategy class</returns>
      public static async Task<GapUpStrategyFutures> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new GapUpStrategyFutures( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private GapUpStrategyFutures( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
         : base( url, apiKey, launchMode, sid, userId )
      {
         // No-Op
      }

      private async Task InitializeAsync()
      {
         _holidays = await GetHolidays( TradeExchange.NSE );

         // Load any saved state
         _state = await GetDataAsync<State>( "state" );

         if ( ( _state == null ) || ( LaunchMode == StrategyLaunchMode.Backtesting ) )
         {
            _state = new State();
            _state.Orders = new List<Order>();
            _state.DayChanged = false;
         }

         // Create our stock symbol object
         // For India users
         _symbolIndex = new Symbol() { SymbolType = SymbolType.Index, Ticker = "NIFTY 50" };

         // For USA users
         //_symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "SPY" };

         // Create the technical indicator evaluator that can work with day candles of the stock
         // This will auto sync with the new tick data that would be coming in for this symbol
         _indicatorEvaluator = await CreateIndicatorEvaluatorAsync( new CreateIndicatorRequest()
         {
            Symbol = _symbolIndex,
            CandlePeriod = CandlePeriod.Day,
            PeriodSpan = 1
         } );

         // Subscribe to the symbols we want (one second tick data)
         await SubscribeSymbolsAsync( new List<Symbol>
         {
            _symbolIndex
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

               if ( order.OrderDirection == OrderDirection.Sell )
               {
                  _state.Bought = true;
                  _state.CurrentOrder = order;

                  // Log the sell (short)
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Sold (Short) {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
               else
               {
                  _state.Bought = false;
                  _state.CurrentOrder = null;

                  // Log the buy (short)
                  var log = $"{order.OrderTimestamp}, Order Id {order.OrderId}, Bought (Short) {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
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
         try
         {
            if ( tickData.Symbol.Equals( _symbolIndex ) )
               _state.IndexCurrentTick = tickData;

            var expiryDate = GetNextMonthlyClosureDay( tickData.Timestamp, DayOfWeek.Thursday );

            if ( ( _symbolCurrentMonthFutures == null ) ||
               ( _symbolCurrentMonthFutures != null ) && ( expiryDate.Date > _symbolCurrentMonthFutures.ExpiryDate.Date ) )
            {
               if ( _symbolCurrentMonthFutures != null )
               {
                  await UnsubscribeSymbolsAsync( new List<Symbol> { _symbolCurrentMonthFutures } );
               }

               _symbolCurrentMonthFutures = new Symbol()
               {
                  ExpiryDate = expiryDate,
                  FNOMonth = 0,
                  FNOPeriodType = FNOPeriodType.Monthly,
                  SymbolType = SymbolType.FuturesIndex,
                  Ticker = "NIFTY"
               };

               await SubscribeSymbolsAsync( new List<Symbol> { _symbolCurrentMonthFutures } );
            }

            if ( tickData.Symbol.Equals( _symbolIndex ) )
               return;

            var prevTick = _state.CurrentTick;
            _state.CurrentTick = tickData;

            if ( prevTick == null || ( !_state.DayChanged && tickData.Timestamp.Day > prevTick.Timestamp.Day && !_state.Bought ) )
               _state.DayChanged = true;

            // Get the gap-up data
            var yesterdayLow = await _indicatorEvaluator.PREVLOWAsync();
            var yesterdayHigh = await _indicatorEvaluator.PREVHIGHAsync();
            var yesterdayClose = await _indicatorEvaluator.PREVCLOSEAsync();
            var todayOpen = await _indicatorEvaluator.OPENAsync();

            if ( ( _state.LastTick == null ) || ( tickData.Timestamp - _state.LastTick.Timestamp ).TotalMinutes >= 1 )
            {
               var log = $"{tickData.Symbol}, {tickData.Timestamp}, {tickData.LTP}, iltp {_state.IndexCurrentTick?.LTP}, yh {yesterdayHigh}, yl {yesterdayLow}, yc {yesterdayClose}, to {todayOpen}";
               await LogAsync( LogLevel.Debug, log );
               _state.LastTick = tickData;

               // DIAG::
               Console.WriteLine( log );
            }

            // We SELL (Short) the stock when the today's open price is higher than or equal to yesterday's high price,
            // AND if the today's open is atleast X percentage greater than yesterday's close.
            if ( yesterdayHigh > 0 && yesterdayClose > 0 &&
               todayOpen >= yesterdayHigh &&
               todayOpen >= ( yesterdayClose + ( yesterdayClose * 0.75 / 100 ) ) &&
               _state.DayChanged &&
               ( !_state.Bought ) && ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
            {
               _state.DayChanged = false;

               // Place sell (short) order
               _state.CurrentOrderId = Guid.NewGuid().ToString();
               var qty = Math.Floor( Capital / tickData.LTP ) * Leverage;

               await PlaceOrderAsync( new PlaceOrderRequest()
               {
                  OrderType = OrderType.Market,
                  Price = tickData.LTP,
                  Quantity = qty,
                  Symbol = _symbolCurrentMonthFutures,
                  Timestamp = tickData.Timestamp,
                  TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
                  TriggerPrice = tickData.LTP,
                  OrderDirection = OrderDirection.Sell,
                  SlippageType = SlippageType.TIME,
                  Slippage = 1000,
                  Tag = _state.CurrentOrderId,
                  OrderProductType = OrderProductType.Intraday
               } );

               // Store our state
               await SetDataAsync( "state", _state );

               // Log the buy initiation
               var log = $"{tickData.Timestamp}, Placed sell (short) order for {qty} units of {_symbolCurrentMonthFutures} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );
            }
            else if ( _state.CurrentOrder != null )
            {
               if ( (
                     ( ( tickData.Timestamp.Hour == 15 ) && ( tickData.Timestamp.Minute >= 15 ) ) ||
                     ( _state.CurrentOrder.AveragePrice - tickData.LTP >= _state.CurrentOrder.AveragePrice * 0.15 / 100 ) )
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
                     Symbol = _symbolCurrentMonthFutures,
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
                  var log = $"{tickData.Timestamp}, Placed buy (short) order for {qty} units of {_symbolCurrentMonthFutures} at price (approx) {tickData.LTP}, {tickData.Timestamp}";
                  await LogAsync( LogLevel.Information, log );

                  // DIAG::
                  Console.WriteLine( log );
               }
            }
         }
         catch ( Exception ex )
         {
            await LogAsync( LogLevel.Error, ex.ToString() );

            // DIAG::
            Console.WriteLine( ex );
         }
         finally
         {
            if ( tickData.Symbol.Equals( _symbolCurrentMonthFutures ) )
               await SendProgressAsync( tickData );
         }
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
         await _indicatorEvaluator.PreloadCandlesAsync( 1, DateTime.UtcNow, tradingRequest.ApiKey, tradingRequest.ApiSecretKey );

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
         await _indicatorEvaluator.PreloadCandlesAsync( 1, backtestRequest.StartDate.AddDays( 1 ), backtestRequest.ApiKey, backtestRequest.ApiSecretKey );

         // Run backtest
         return await base.BacktestAsync( backtestRequest );
      }

      private async Task<StrategyRunSummary> GetStrategyRunSummaryAsync()
      {
         if ( _symbolCurrentMonthFutures == null )
            return null;

         var symbolState = _symbolCurrentMonthFutures;

         var summary = await GetStrategyRunSummaryAsync( Capital, ( symbolState.CurrentTick != null ? new List<KeyValuePair<Symbol, TickData>>()
            {
               new KeyValuePair<Symbol, TickData>(_symbolOptionsPE, symbolState.CurrentTick)
            } : null ) );

         return summary;
      }

      public async override Task<Dictionary<string, object>> GetStatsAsync( TickData tickData )
      {
         if ( _symbolOptionsPE == null )
            return new Dictionary<string, object>();

         try
         {
            var statsMap = new Dictionary<string, object>();
            var symbolState = _stateMap[_symbolOptionsPE];

            var summary = await GetStrategyRunSummaryAsync();

            statsMap.Add( "Total Capital", summary.Capital );
            statsMap.Add( "Total PL", summary.PL );
            statsMap.Add( "Total Max Draw Down", summary.MaxDrawdown );

            KeyValuePair<Symbol, Dictionary<string, object>> stats = default( KeyValuePair<Symbol, Dictionary<string, object>> );

            if ( summary != null && summary.SymbolStats != null )
            {
               stats = summary.SymbolStats.FirstOrDefault( obj => obj.Key.Equals( _symbolOptionsPE ) );

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

      public DateTime GetNextWeeklyClosureDay( DateTime date, DayOfWeek dayofweek )
      {
         var checkDate = new DateTime( date.Year, date.Month, date.Day );
         int daysDiff = dayofweek - checkDate.DayOfWeek;

         if ( daysDiff < 0 )
            daysDiff = (int) checkDate.DayOfWeek - daysDiff;

         var dt = checkDate.AddDays( daysDiff );

         while ( true )
         {
            var contains = _holidays.Contains( dt );

            if ( contains || dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday )
               dt = dt.AddDays( -1 );
            else
               break;
         }

         return dt;
      }

      public DateTime GetNextMonthlyClosureDay( DateTime date, DayOfWeek dayofweek )
      {
         var dt = date.GetLastSpecificDayOfTheMonth( dayofweek );

         while ( true )
         {
            var contains = _holidays.Contains( dt );

            if ( contains || dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday )
               dt = dt.AddDays( -1 );
            else
               break;
         }

         return dt;
      }
   }
}