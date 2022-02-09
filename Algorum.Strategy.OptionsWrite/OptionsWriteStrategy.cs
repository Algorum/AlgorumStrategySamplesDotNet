using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Algorum.Strategy.OptionsWrite
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   /// <remarks>
   /// This strategy shows how to implement weekly options selling/writing based on NIFTY 50 index movement.
   /// This strategy shows how to choose call or put options for selling/writing dynamically based on some criteria.
   /// This strategy shows how to dynamically subscribe to and unsubscribe from the weekly changing options contracts tickers.
   /// This strategy shows how to calculate a simple P&L for the strategy.
   /// This strategy is NOT an advice or recommendation. This is solely aimed at educating Algorum users in implementing 
   /// different well known trading techniques in the Capital Markets.
   /// More about call & put options writing can be learned from Zerodha Varsity at the below URL...
   /// https://zerodha.com/varsity/chapter/sellingwriting-a-call-option/
   /// </remarks>
   public class OptionsWriteStrategy : QuantEngineClient
   {
      /// <summary>
      /// Global state for the strategy.
      /// </summary>
      private class StateGlobal
      {
         public volatile bool Bought;
         public volatile bool Processing;
         public volatile bool SubscribedSymbols;
         public volatile bool ShouldEnterTrade;
         public DateTime SubscribedDate;
         public List<Order> Orders;
         public bool PeriodChanged;
         public double EnteredPL;
      }

      /// <summary>
      /// State of individual weekly contracts
      /// </summary>
      private class State
      {
         public volatile bool Bought;
         public TickData CurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public volatile bool Short;
         public TickData PrevTick;
         public CrossBelow CrossBelowUpObj;
         public CrossBelow CrossBelowDownObj;
      }

      public const double Capital = 1000000;

      private const int UP_DIRECTION = 1;
      private const int DOWN_DIRECTION = 2;
      private const int SIGNAL_STRENGTH = 7;
      private const double SPOT_STRIKE_DISTANCE_PERCENT = 2.0;
      private const int LOT_SIZE_2020 = 75;
      private const int LOT_SIZE_2021 = 50;
      private const int STRIKE_DISTANCE = 50;
      private const double Leverage = 10; // 10x Leverage (based on margin available for NIFTY options. may change. adjust accordingly)

      private const string INDEX_TICKER = "NIFTY 50";
      private const string OPTIONS_TICKER = "NIFTY";

      private Symbol _symbol;
      private Symbol _symbolOptionsPE;
      private IIndicatorEvaluator _indicatorEvaluator;
      private ConcurrentDictionary<Symbol, State> _stateMap = new ConcurrentDictionary<Symbol, State>();
      private StateGlobal _state = new StateGlobal();
      private List<DateTime> _holidays;


      /// <summary>
      /// Helps create OptionsWriteStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <returns>Instance of OptionsWriteStrategy class</returns>
      public static async Task<OptionsWriteStrategy> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new OptionsWriteStrategy( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private OptionsWriteStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
         : base( url, apiKey, launchMode, sid, userId )
      {
         // No-Op
      }

      private async Task InitializeAsync()
      {
         // Load any saved state
         try
         {
            _stateMap = new ConcurrentDictionary<Symbol, State>( await GetDataAsync<List<KeyValuePair<Symbol, State>>>( "state_map" ) );
            _state = await GetDataAsync<StateGlobal>( "state" );
         }
         catch ( Exception ex )
         {
            await LogAsync( LogLevel.Error, ex.ToString() );
         }

         // If we do not have state or we are running in backtesting mode, initialize our state
         if ( ( _state == null ) || ( _stateMap == null ) || ( LaunchMode == StrategyLaunchMode.Backtesting ) )
         {
            _stateMap = new ConcurrentDictionary<Symbol, State>();
            _state = new StateGlobal();
            _state.Orders = new List<Order>();
         }

         // Create base Index object
         _symbol = new Symbol()
         {
            SymbolType = SymbolType.Index,
            Ticker = INDEX_TICKER
         };

         // Create the technical indicator evaluator that can work with day candles of the stock
         // This will auto sync with the new tick data that would be coming in for this symbol
         _indicatorEvaluator = await CreateIndicatorEvaluatorAsync( new CreateIndicatorRequest()
         {
            Symbol = _symbol,
            CandlePeriod = CandlePeriod.Minute,
            PeriodSpan = 60
         } );

         // Subscribe to the symbols we want (one second tick data)
         await SubscribeSymbolsAsync( new List<Symbol>
         {
            _symbol
         } );

         _holidays = await GetHolidaysAsync( TradeExchange.NSE );
      }

      /// <summary>
      /// Get or create & initialize a new state for the given symbol
      /// </summary>
      /// <param name="symbol">Symbol object for which state has to be retrieved or created</param>
      /// <returns>State object</returns>
      private State GetSymbolState( Symbol symbol )
      {
         lock ( _stateMap )
         {
            if ( _stateMap.TryGetValue( symbol, out var state ) )
            {
               return state;
            }
            else
            {
               state = new State();
               state.Orders = new List<Order>();
               state.CrossBelowUpObj = new CrossBelow();
               state.CrossBelowDownObj = new CrossBelow();
               _stateMap[symbol] = state;
               return state;
            }
         }
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
            var state = GetSymbolState( order.Symbol );
            List<State> otherStates = new List<State>();
            otherStates.Add( GetSymbolState( _symbolOptionsPE ) );

            switch ( order.Status )
            {
            case OrderStatus.Cancelled:
               {
                  lock ( _state )
                     _state.Orders.Add( order );

                  state.Bought = false;
                  state.CurrentOrder = null;

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

               if ( !state.Short )
               {
                  if ( order.OrderDirection == OrderDirection.Buy )
                  {
                     state.Bought = true;
                     state.CurrentOrder = order;
                     state.CurrentOrder.Short = state.Short;

                     // Log the buy
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => !obj.Bought ) == null )
                     {
                        _state.Bought = true;
                        _state.Processing = false;

                        var summary = await GetStrategyRunSummaryAsync();

                        if ( summary != null )
                           _state.EnteredPL = summary.PL;
                     }
                  }
                  else
                  {
                     state.Bought = false;
                     state.CurrentOrder = null;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => obj.Bought ) == null )
                     {
                        _state.Bought = false;
                        _state.Processing = false;
                        _state.EnteredPL = 0;
                     }
                  }
               }
               else
               {
                  if ( order.OrderDirection == OrderDirection.Sell )
                  {
                     state.Bought = true;
                     state.CurrentOrder = order;
                     state.CurrentOrder.Short = state.Short;

                     // Log the buy
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => !obj.Bought ) == null )
                     {
                        _state.Bought = true;
                        _state.Processing = false;

                        var summary = await GetStrategyRunSummaryAsync();

                        if ( summary != null )
                           _state.EnteredPL = summary.PL;
                     }
                  }
                  else
                  {
                     state.Bought = false;
                     state.CurrentOrder = null;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => obj.Bought ) == null )
                     {
                        _state.Bought = false;
                        _state.Processing = false;
                        _state.EnteredPL = 0;
                     }
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

            state.CurrentOrderId = string.Empty;

            var stats = await GetStatsAsync( state.CurrentTick );
            await SendAsync( "publish_stats", stats );

            foreach ( var kvp in stats )
            {
               var log = $"{kvp.Key}: {kvp.Value}";
               await LogAsync( LogLevel.Information, log );

               // DIAG::
               Console.WriteLine( log );
            }

            // Store our state
            await SetDataAsync( "state_map", _stateMap );
            await SetDataAsync( "state", _state );
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
            var state = GetSymbolState( tickData.Symbol );
            state.PrevTick = state.CurrentTick;
            state.CurrentTick = tickData;

            var weeklyExpiryDate = await GetNextWeeklyClosureDayAsync( _state.SubscribedDate, DayOfWeek.Thursday, _holidays );

            if ( tickData.Symbol.Equals( _symbol ) )
            {
               var (direction, strength) = await _indicatorEvaluator.TRENDAsync( 35 );

               // DIAG::
               Console.WriteLine( $"D {direction}, S {strength}, DOW {tickData.Timestamp.DayOfWeek}" );

               if ( !_state.SubscribedSymbols )
               {
                  // When we reach the next options week,
                  // dynamically unsubscribe and subscribe to the options contracts,
                  // based on desired conditions.
                  if ( tickData.Timestamp.Date > weeklyExpiryDate.Date &&
                     tickData.Timestamp.Hour >= 11 &&
                     ( tickData.Timestamp.DayOfWeek == DayOfWeek.Tuesday ||
                     tickData.Timestamp.DayOfWeek == DayOfWeek.Wednesday ) )
                  {
                     if ( ( direction == UP_DIRECTION && state.CrossBelowUpObj.Evaluate( strength, SIGNAL_STRENGTH ) ) ||
                        ( direction == DOWN_DIRECTION && state.CrossBelowDownObj.Evaluate( strength, SIGNAL_STRENGTH ) ) )
                     {
                        _state.ShouldEnterTrade = true;
                        _state.SubscribedSymbols = true;
                        _state.SubscribedDate = tickData.Timestamp;

                        var expiryDate = await GetNextWeeklyClosureDayAsync( _state.SubscribedDate, DayOfWeek.Thursday, _holidays );

                        // Calculate our entries
                        var todayOpen = await _indicatorEvaluator.OPENAsync();

                        double shortPE = 0;
                        OptionType optionType = OptionType.CE;

                        if ( direction == UP_DIRECTION )
                        {
                           // If trend direction is identified as going UP, then write/sell Out of Money (OTM) call option
                           shortPE = todayOpen + ( todayOpen * SPOT_STRIKE_DISTANCE_PERCENT / 100 );
                           shortPE += STRIKE_DISTANCE - ( shortPE % STRIKE_DISTANCE );

                           optionType = OptionType.CE;
                        }
                        else if ( direction == DOWN_DIRECTION )
                        {
                           // If trend direction is identified as going DOWN, then write/sell Out of Money (OTM) put option
                           shortPE = todayOpen - ( todayOpen * SPOT_STRIKE_DISTANCE_PERCENT / 100 );
                           shortPE -= shortPE % STRIKE_DISTANCE;

                           optionType = OptionType.PE;
                        }

                        _symbolOptionsPE = new Symbol()
                        {
                           SymbolType = SymbolType.OptionsIndex,
                           Ticker = OPTIONS_TICKER,
                           FNOPeriodType = FNOPeriodType.Weekly,
                           OptionType = optionType,
                           OptionValue = shortPE,
                           ExpiryDate = expiryDate
                        };

                        await SubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsPE
                        } );

                     }
                  }
               }
               else
               {
                  if ( tickData.Timestamp.Date > weeklyExpiryDate.Date )
                  {
                     _state.ShouldEnterTrade = false;
                     _state.PeriodChanged = true;
                     _state.SubscribedSymbols = false;

                     if ( _symbolOptionsPE != null )
                     {
                        await UnsubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsPE
                        } );
                     }
                  }
               }
            }

            if ( _symbolOptionsPE == null )
               return;

            var idxState = GetSymbolState( _symbol );
            var optionState = GetSymbolState( _symbolOptionsPE );

            if ( ( optionState?.CurrentTick == null ) || ( idxState?.CurrentTick == null ) )
               return;

            var weeklyClosureDate = await GetNextWeeklyClosureDayAsync( idxState.CurrentTick.Timestamp, DayOfWeek.Thursday, _holidays );
            await LogAsync( LogLevel.Debug, $"{tickData.Timestamp}, {tickData.Symbol.Ticker}, {tickData.Symbol.SymbolType}, {tickData.Symbol.OptionType}, {tickData.Symbol.OptionValue}, {tickData.LTP}, B {_state.Bought}, P {_state.Processing}, WC {weeklyExpiryDate.ToString( "dd-MMM-yyyy" )}, WC-DOW {weeklyExpiryDate.DayOfWeek}" );

            var summary = await GetStrategyRunSummaryAsync();

            // Enter when the weekly options start after the above weekly subscription conditions are met.
            // Enter only after 11 AM, to not jump in during first few hours of volatility
            // Do not enter if the current day is expiry day.
            if (
               _state.ShouldEnterTrade &&
               !_state.Processing &&
               !_state.Bought &&
               tickData.Timestamp.Hour >= 11 &&
               tickData.Timestamp.Date != weeklyExpiryDate.Date )
            {
               _state.ShouldEnterTrade = false;
               _state.Processing = true;

               try
               {
                  await EnterOrderAsync( idxState, optionState, summary.PL );
               }
               catch
               {
                  _state.Processing = false;
                  throw;
               }
            }
            // Exit from the options positions...
            // If the spot price hits the option strike price (stop loss) .OR.
            // On the expiry day after 3 PM before market close).
            else if (
                        !_state.Processing &&
                        _state.Bought &&
                        (
                           (
                              ( ( optionState.CurrentOrder.Symbol.OptionType == OptionType.PE ) && ( idxState.CurrentTick.LTP <= optionState.CurrentOrder.Symbol.OptionValue ) ) ||
                              ( ( optionState.CurrentOrder.Symbol.OptionType == OptionType.CE ) && ( idxState.CurrentTick.LTP >= optionState.CurrentOrder.Symbol.OptionValue ) )
                           ) ||
                           (
                              idxState.CurrentTick.Timestamp.Hour == 15 &&
                              ( weeklyExpiryDate.Date == idxState.CurrentTick.Timestamp.Date )
                           )
                        )
                    )
            {
               _state.Processing = true;

               try
               {
                  await ExitOrderAsync( idxState, optionState );
               }
               catch
               {
                  _state.Processing = false;
                  throw;
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
            await SendProgressAsync( tickData );
         }
      }

      private async Task EnterOrderAsync( State idxState, State optionState, double pl )
      {
         optionState.Short = true;

         // Place short order for the option
         int lotSize = idxState.CurrentTick.Timestamp.Date < new DateTime( 2021, 4, 1 ).Date ? LOT_SIZE_2020 : LOT_SIZE_2021;
         var qty = (double) (int) ( ( ( Capital + pl ) * Leverage ) / GetSymbolState( _symbol ).CurrentTick.LTP );
         qty = qty - ( qty % lotSize );
         optionState.CurrentOrderId = Guid.NewGuid().ToString();

         var placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = optionState.CurrentTick.LTP,
            Quantity = qty,
            Symbol = _symbolOptionsPE,
            Timestamp = optionState.CurrentTick.Timestamp,
            TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
            TriggerPrice = optionState.CurrentTick.LTP,
            OrderDirection = OrderDirection.Sell,
            Tag = optionState.CurrentOrderId,
            OrderProductType = OrderProductType.Normal,
            SlippageType = SlippageType.TIME,
            Slippage = 1000
         };

         await PlaceOrderAsync( placeOrderRequest );

         // Store our state
         await SetDataAsync( "state_map", _stateMap );

         // Log the buy initiation
         var log = $"{optionState.CurrentTick.Timestamp}, Spot, {idxState.CurrentTick.LTP}, Placed, sell (short), order for, {qty}, units of, {_symbolOptionsPE.GetInstrumentIdentifier()}, at price (approx), {( placeOrderRequest.OrderType == OrderType.Market ? optionState.CurrentTick.LTP : placeOrderRequest.Price )}, {optionState.CurrentTick.Timestamp}";
         await LogAsync( LogLevel.Information, log );

         // DIAG::
         Console.WriteLine( log );
      }

      private async Task ExitOrderAsync( State idxState, State optionState )
      {
         // Place short order for the option
         var qty = optionState.CurrentOrder.FilledQuantity;
         optionState.CurrentOrderId = Guid.NewGuid().ToString();

         var placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = optionState.CurrentTick.LTP,
            Quantity = qty,
            Symbol = _symbolOptionsPE,
            Timestamp = optionState.CurrentTick.Timestamp,
            TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
            TriggerPrice = optionState.CurrentTick.LTP,
            OrderDirection = OrderDirection.Buy,
            Tag = optionState.CurrentOrderId,
            OrderProductType = OrderProductType.Normal,
            SlippageType = SlippageType.TIME,
            Slippage = 1000
         };

         await PlaceOrderAsync( placeOrderRequest );

         // Store our state
         await SetDataAsync( "state_map", _stateMap );

         // Log the buy initiation
         var log = $"{optionState.CurrentTick.Timestamp}, Spot, {idxState.CurrentTick.LTP}, Placed, buy (short), order for, {qty}, units of, {_symbolOptionsPE.GetInstrumentIdentifier()}, at price (approx), {( placeOrderRequest.OrderType == OrderType.Market ? optionState.CurrentTick.LTP : placeOrderRequest.Price )}, {optionState.CurrentTick.Timestamp}";
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
         // Preload candles
         await _indicatorEvaluator.PreloadCandlesAsync( 1, DateTime.UtcNow, tradingRequest.ApiKey, tradingRequest.ApiSecretKey );

         // Start trading
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
         await _indicatorEvaluator.PreloadCandlesAsync( 5, backtestRequest.StartDate.AddDays( -1 ), backtestRequest.ApiKey, backtestRequest.ApiSecretKey );

         // Start backtest
         return await base.BacktestAsync( backtestRequest );
      }

      private async Task<StrategyRunSummary> GetStrategyRunSummaryAsync()
      {
         if ( _symbolOptionsPE == null )
            return null;

         var symbolState = _stateMap[_symbolOptionsPE];

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
   }
}