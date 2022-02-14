using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Algorum.Strategy.BearBullSpread
{
   /// <summary>
   /// Strategy classes should derive from the QuantEngineClient, 
   /// and implement the abstract methods to receive events like 
   /// tick data, order update, etc.,
   /// </summary>
   public class BearBullSpreadStrategy : QuantEngineClient
   {
      private class StateGlobal
      {
         public volatile bool Bought;
         public volatile bool Processing;
         public volatile bool SubscribedSymbols;
         public DateTime SubscribedDate;
         public List<Order> Orders;
         public bool PeriodChanged;
         public double EnteredPL;
         public volatile bool ShouldEnterTrade;
      }

      private class State
      {
         public volatile bool Bought;
         public TickData CurrentTick;
         public List<Order> Orders;
         public string CurrentOrderId;
         public Order CurrentOrder;
         public volatile bool ShouldEnterTrade;
         public volatile bool Short;
         public TickData PrevTick;
         public CrossBelow CrossBelowUpObj;
         public CrossBelow CrossBelowDownObj;
      }

      public const double Capital = 4000000;

      private const int UP_DIRECTION = 1;
      private const int DOWN_DIRECTION = 2;
      private const int SIGNAL_STRENGTH = 7;
      private const double STRIKE_DISTANCE_PERCENT = 2.0;
      private const int LOT_SIZE_2020 = 75;
      private const int LOT_SIZE_2021 = 50;
      private const int STRIKE_DISTANCE = 50;
      private const int SPREAD_DISTANCE = 100;
      private const double Leverage = 15; // 15x Leverage (based on margin available for NIFTY options. may change. adjust accordingly)

      private const string INDEX_TICKER = "NIFTY 50";
      private const string OPTIONS_TICKER = "NIFTY";

      private Symbol _symbol;
      private Symbol _symbolOptionsShort;
      private Symbol _symbolOptionsLong;
      private IIndicatorEvaluator _indicatorEvaluator;
      private ConcurrentDictionary<Symbol, State> _stateMap = new ConcurrentDictionary<Symbol, State>();
      private StateGlobal _state = new StateGlobal();
      private List<DateTime> _holidays;


      /// <summary>
      /// Helps create BearBullSpreadStrategy class and initialize asynchornously
      /// </summary>
      /// <param name="url">URL of the Quant Engine Server</param>
      /// <param name="apiKey">User Algorum API Key</param>
      /// <param name="launchMode">Launch mode of this strategy</param>
      /// <param name="sid">Unique Strategy Id</param>
      /// <returns>Instance of GoldenCrossoverQuantStrategy class</returns>
      public static async Task<BearBullSpreadStrategy> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new BearBullSpreadStrategy( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private BearBullSpreadStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
         : base( url, apiKey, launchMode, sid, userId )
      {
         // No-Op
      }

      private async Task InitializeAsync()
      {
         // Load any saved state
         if ( ( _stateMap == null ) || ( LaunchMode == StrategyLaunchMode.Backtesting ) )
         {
            _stateMap = new ConcurrentDictionary<Symbol, State>();
            _state = new StateGlobal();
            _state.Orders = new List<Order>();
         }

         // Create our stock symbol object
         _symbol = new Symbol()
         {
            SymbolType = SymbolType.Index,
            Ticker = INDEX_TICKER
         };

         // Create the technical indicator evaluator that can work with minute candles of the index
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
            otherStates.Add( GetSymbolState( _symbolOptionsShort ) );
            otherStates.Add( GetSymbolState( _symbolOptionsLong ) );

            var stats = await GetStatsAsync( state.CurrentTick );
            stats.TryGetValue( "Total PL", out var plObj );
            double pl = ( plObj != null ) ? (double) plObj : 0;

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
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, {order.Symbol.GetInstrumentIdentifier()}, Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}, asked price {order.Price}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => !obj.Bought ) == null )
                     {
                        _state.Bought = true;
                        _state.Processing = false;
                        _state.EnteredPL = pl;

                        // DIAG::
                        Console.WriteLine( $"{order.OrderTimestamp.Value} Entered PL {pl}" );
                        await LogAsync( LogLevel.Debug, $"{order.OrderTimestamp.Value} Entered PL {pl}" );
                     }
                  }
                  else
                  {
                     state.Bought = false;
                     state.CurrentOrder = null;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, {order.Symbol.GetInstrumentIdentifier()}, Sold {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}, asked price {order.Price}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => obj.Bought ) == null )
                     {
                        _state.Bought = false;
                        _state.Processing = false;
                        _state.EnteredPL = 0;

                        // DIAG::
                        Console.WriteLine( $"{order.OrderTimestamp.Value} Entered PL {pl}" );
                        await LogAsync( LogLevel.Debug, $"{order.OrderTimestamp.Value} Entered PL {pl}" );
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
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, {order.Symbol.GetInstrumentIdentifier()}, Short Sell {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}, asked price {order.Price}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => !obj.Bought ) == null )
                     {
                        _state.Bought = true;
                        _state.Processing = false;
                        _state.EnteredPL = pl;

                        // DIAG::
                        Console.WriteLine( $"{order.OrderTimestamp.Value} Entered PL {pl}" );
                        await LogAsync( LogLevel.Debug, $"{order.OrderTimestamp.Value} Entered PL {pl}" );
                     }
                  }
                  else
                  {
                     state.Bought = false;
                     state.CurrentOrder = null;

                     // Log the sell
                     var log = $"{order.OrderTimestamp}, Completed Order Id {order.OrderId}, {order.Symbol.GetInstrumentIdentifier()}, Short Bought {order.FilledQuantity} units of {order.Symbol.Ticker} at price {order.AveragePrice}, asked price {order.Price}";
                     await LogAsync( LogLevel.Information, log );

                     // DIAG::
                     Console.WriteLine( log );

                     if ( otherStates.FirstOrDefault( obj => obj.Bought ) == null )
                     {
                        _state.Bought = false;
                        _state.Processing = false;
                        _state.EnteredPL = 0;

                        // DIAG::
                        Console.WriteLine( $"{order.OrderTimestamp.Value} Entered PL {pl}" );
                        await LogAsync( LogLevel.Debug, $"{order.OrderTimestamp.Value} Entered PL {pl}" );
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

            stats = await GetStatsAsync( state.CurrentTick );
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
               var (direction, strength) = await _indicatorEvaluator.TRENDAsync( 20 );

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
                     // If the direction is strong DOWN and started falling back, then we...
                     // 1. SELL/WRITE a OTM PUT option
                     // 2. BUY a OTM PUT option further below SHORT option strike price
                     // (assuming that the price will not go down beyond the selected option strike price,
                     // because the price has already moved down enough)
                     if ( direction == DOWN_DIRECTION && state.CrossBelowUpObj.Evaluate( strength, SIGNAL_STRENGTH ) )
                     {
                        _state.ShouldEnterTrade = true;
                        _state.SubscribedSymbols = true;
                        _state.SubscribedDate = tickData.Timestamp;

                        var expiryDate = await GetNextWeeklyClosureDayAsync( _state.SubscribedDate, DayOfWeek.Thursday, _holidays );

                        // Calculate our entries
                        var todayOpen = await _indicatorEvaluator.PREVCLOSEAsync();

                        var shortOptionStrike = todayOpen - ( todayOpen * STRIKE_DISTANCE_PERCENT / 100 );
                        shortOptionStrike -= shortOptionStrike % STRIKE_DISTANCE;

                        var longOptionStrike = shortOptionStrike - SPREAD_DISTANCE;

                        // DIAG::
                        Console.WriteLine( $"Short Strike {shortOptionStrike}, Long Strike {longOptionStrike}" );

                        _symbolOptionsShort = new Symbol()
                        {
                           SymbolType = SymbolType.OptionsIndex,
                           Ticker = OPTIONS_TICKER,
                           FNOPeriodType = FNOPeriodType.Weekly,
                           OptionType = OptionType.PE,
                           OptionValue = shortOptionStrike,
                           ExpiryDate = expiryDate
                        };

                        await SubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsShort
                        } );

                        _symbolOptionsLong = new Symbol()
                        {
                           SymbolType = SymbolType.OptionsIndex,
                           Ticker = OPTIONS_TICKER,
                           FNOPeriodType = FNOPeriodType.Weekly,
                           OptionType = OptionType.PE,
                           OptionValue = longOptionStrike,
                           ExpiryDate = expiryDate
                        };

                        await SubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsLong
                        } );
                     }
                     // If the direction is strong UP and started falling back, then we ... 
                     // 1. SELL/WRITE a OTM CALL option
                     // 2. BUY a OTM CALL option further below SHORT option strike price
                     // (assuming that the price will not go up beyond the selected option strike price,
                     // because the price has already moved up enough)
                     else if ( direction == UP_DIRECTION && state.CrossBelowUpObj.Evaluate( strength, SIGNAL_STRENGTH ) )
                     {
                        _state.ShouldEnterTrade = true;
                        _state.SubscribedSymbols = true;
                        _state.SubscribedDate = tickData.Timestamp;

                        var expiryDate = await GetNextWeeklyClosureDayAsync( _state.SubscribedDate, DayOfWeek.Thursday, _holidays );

                        // Calculate our entries
                        var todayOpen = await _indicatorEvaluator.PREVCLOSEAsync();

                        var shortOptionStrike = todayOpen + ( todayOpen * STRIKE_DISTANCE_PERCENT / 100 );
                        shortOptionStrike += STRIKE_DISTANCE - ( shortOptionStrike % STRIKE_DISTANCE );

                        var longOptionStrike = shortOptionStrike + SPREAD_DISTANCE;

                        // DIAG::
                        Console.WriteLine( $"ShortPE {shortOptionStrike}, LongPE {longOptionStrike}" );

                        _symbolOptionsShort = new Symbol()
                        {
                           SymbolType = SymbolType.OptionsIndex,
                           Ticker = OPTIONS_TICKER,
                           FNOPeriodType = FNOPeriodType.Weekly,
                           OptionType = OptionType.CE,
                           OptionValue = shortOptionStrike,
                           ExpiryDate = expiryDate
                        };

                        await SubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsShort
                        } );

                        _symbolOptionsLong = new Symbol()
                        {
                           SymbolType = SymbolType.OptionsIndex,
                           Ticker = OPTIONS_TICKER,
                           FNOPeriodType = FNOPeriodType.Weekly,
                           OptionType = OptionType.CE,
                           OptionValue = longOptionStrike,
                           ExpiryDate = expiryDate
                        };

                        await SubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsLong
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

                     if ( _symbolOptionsShort != null )
                     {
                        await UnsubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsShort
                        } );
                     }

                     if ( _symbolOptionsLong != null )
                     {
                        await UnsubscribeSymbolsAsync( new List<Symbol>
                        {
                           _symbolOptionsLong
                        } );
                     }
                  }
               }

            }

            if ( _symbolOptionsShort == null || _symbolOptionsLong == null )
               return;

            var idxState = GetSymbolState( _symbol );
            var shortOptionState = GetSymbolState( _symbolOptionsShort );
            var longOptionState = GetSymbolState( _symbolOptionsLong );

            if ( ( shortOptionState?.CurrentTick == null ) || ( idxState?.CurrentTick == null ) ||
               ( longOptionState?.CurrentTick == null ) )
               return;

            if ( ( shortOptionState.CurrentTick.Timestamp.Hour != state.CurrentTick.Timestamp.Hour ) ||
               ( idxState.CurrentTick.Timestamp.Hour != state.CurrentTick.Timestamp.Hour ) ||
               ( longOptionState.CurrentTick.Timestamp.Hour != state.CurrentTick.Timestamp.Hour ) )
               return;

            var weeklyClosureDate = await GetNextWeeklyClosureDayAsync( idxState.CurrentTick.Timestamp, DayOfWeek.Thursday, _holidays );
            await LogAsync( LogLevel.Debug, $"{tickData.Timestamp}, {tickData.Symbol.Ticker}, {tickData.Symbol.SymbolType}, {tickData.Symbol.OptionType}, {tickData.Symbol.OptionValue}, {tickData.LTP}, B {_state.Bought}, P {_state.Processing}, WC {weeklyClosureDate.ToString( "dd-MMM-yyyy" )}, WC-DOW {weeklyClosureDate.DayOfWeek}" );

            var summary = await GetStrategyRunSummaryAsync();

            // Enter when the weekly options start after the above weekly subscription conditions are met.
            // Enter only after 11 AM, to not jump in during first few hours of volatility
            // Do not enter if the current day is expiry day.
            if (
               _state.ShouldEnterTrade &&
               !_state.Processing &&
               !_state.Bought &&
               tickData.Timestamp.Date != weeklyClosureDate.Date &&
               state.CurrentTick.Timestamp.Hour >= 11 )
            {
               // We enter position if the option premium is not overly priced (beyond the diff between the spot and option strike prices)
               if (
                     (
                        ( shortOptionState.CurrentTick.Symbol.OptionType == OptionType.PE ) &&
                        ( idxState.CurrentTick.LTP - shortOptionState.CurrentTick.Symbol.OptionValue > shortOptionState.CurrentTick.LTP )
                     ) ||
                     (
                        ( shortOptionState.CurrentTick.Symbol.OptionType == OptionType.CE ) &&
                        ( shortOptionState.CurrentTick.Symbol.OptionValue - idxState.CurrentTick.LTP > shortOptionState.CurrentTick.LTP )
                     )
                  )
               {
                  _state.ShouldEnterTrade = false;
                  _state.Processing = true;

                  try
                  {
                     await EnterOrderAsync( idxState, shortOptionState, longOptionState, summary.PL );
                  }
                  catch
                  {
                     _state.Processing = false;
                     throw;
                  }
               }
               else
               {
                  var log = $"Rejected Entry. Spot {idxState.CurrentTick.LTP}, Option Value {shortOptionState.CurrentTick.Symbol.OptionValue}, Option LTP {shortOptionState.CurrentTick.LTP}";
                  Console.WriteLine( log );
                  await LogAsync( LogLevel.Information, log );
               }
            }
            else if (
                        !_state.Processing &&
                        _state.Bought &&
                        (
                           (
                              ( ( idxState.CurrentTick.Timestamp.Hour > 9 ) && ( shortOptionState.CurrentOrder.Symbol.OptionType == OptionType.PE ) && ( idxState.CurrentTick.LTP <= shortOptionState.CurrentOrder.Symbol.OptionValue ) ) ||
                              ( ( idxState.CurrentTick.Timestamp.Hour > 9 ) && ( shortOptionState.CurrentOrder.Symbol.OptionType == OptionType.CE ) && ( idxState.CurrentTick.LTP >= shortOptionState.CurrentOrder.Symbol.OptionValue ) )
                           ) ||
                           ( idxState.CurrentTick.Timestamp.Hour == 15 &&
                           ( weeklyClosureDate.Date == idxState.CurrentTick.Timestamp.Date ) )
                        )
                    )
            {
               _state.Processing = true;

               try
               {
                  await ExitOrderAsync( idxState, shortOptionState, longOptionState );
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

      private async Task<StrategyRunSummary> GetStrategyRunSummaryAsync()
      {
         if ( _symbolOptionsShort == null || _symbolOptionsLong == null )
            return null;

         var shortOptionSymbolState = _stateMap[_symbolOptionsShort];
         var longOptionSymbolState = _stateMap[_symbolOptionsLong];

         var summary = await GetStrategyRunSummaryAsync( Capital,
            (
            shortOptionSymbolState.CurrentTick != null &&
            longOptionSymbolState.CurrentTick != null ? new List<KeyValuePair<Symbol, TickData>>()
            {
               new KeyValuePair<Symbol, TickData>(_symbolOptionsShort, shortOptionSymbolState.CurrentTick),
               new KeyValuePair<Symbol, TickData>(_symbolOptionsLong, longOptionSymbolState.CurrentTick),
            } : null ), StatsType.ExpiryPairedOptions, 4 );

         return summary;
      }

      private async Task EnterOrderAsync( State idxState, State optionState, State optionStateLong, double pl )
      {
         optionState.Short = true;
         optionStateLong.Short = false;

         // Place short order for CE
         int lotSize = idxState.CurrentTick.Timestamp.Date < new DateTime( 2021, 4, 1 ).Date ? LOT_SIZE_2020 : LOT_SIZE_2021;
         var qty = (double) (int) ( ( ( Capital + pl ) * Leverage ) / GetSymbolState( _symbol ).CurrentTick.LTP );
         qty = qty - ( qty % lotSize );

         // Place short order for PE
         optionState.CurrentOrderId = Guid.NewGuid().ToString();

         var placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = optionState.CurrentTick.LTP,
            Quantity = qty,
            Symbol = _symbolOptionsShort,
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
         var log = $"{optionState.CurrentTick.Timestamp}, Spot, {idxState.CurrentTick.LTP}, Placed, sell (short), order for, {qty}, units of, {_symbolOptionsShort.GetInstrumentIdentifier()}, at price (approx), {( placeOrderRequest.OrderType == OrderType.Market ? optionState.CurrentTick.LTP : placeOrderRequest.Price )}, {optionState.CurrentTick.Timestamp}";
         await LogAsync( LogLevel.Information, log );

         // DIAG::
         Console.WriteLine( log );

         // Long orders
         // Place long order for PE
         optionStateLong.CurrentOrderId = Guid.NewGuid().ToString();

         placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = optionStateLong.CurrentTick.LTP,
            Quantity = qty,
            Symbol = _symbolOptionsLong,
            Timestamp = optionStateLong.CurrentTick.Timestamp,
            TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
            TriggerPrice = optionStateLong.CurrentTick.LTP,
            OrderDirection = OrderDirection.Buy,
            Tag = optionStateLong.CurrentOrderId,
            OrderProductType = OrderProductType.Normal,
            SlippageType = SlippageType.TIME,
            Slippage = 1000
         };

         await PlaceOrderAsync( placeOrderRequest );

         // Store our state
         await SetDataAsync( "state_map", _stateMap );

         // Log the buy initiation
         log = $"{optionStateLong.CurrentTick.Timestamp}, Spot, {idxState.CurrentTick.LTP}, Placed, buy, order for, {qty}, units of, {_symbolOptionsLong.GetInstrumentIdentifier()}, at price (approx), {( placeOrderRequest.OrderType == OrderType.Market ? optionStateLong.CurrentTick.LTP : placeOrderRequest.Price )}, {optionStateLong.CurrentTick.Timestamp}";
         await LogAsync( LogLevel.Information, log );

         // DIAG::
         Console.WriteLine( log );
      }

      private async Task ExitOrderAsync( State idxState, State optionState, State optionStateLong )
      {
         optionState.ShouldEnterTrade = false;
         optionStateLong.ShouldEnterTrade = false;

         // Place short order for PE
         optionState.CurrentOrderId = Guid.NewGuid().ToString();
         var qty = optionState.CurrentOrder.FilledQuantity;

         var placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = optionState.CurrentTick.LTP,
            Quantity = qty,
            Symbol = _symbolOptionsShort,
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
         var log = $"{optionState.CurrentTick.Timestamp}, Spot, {idxState.CurrentTick.LTP}, Placed, buy (short), order for, {qty}, units of, {_symbolOptionsShort.GetInstrumentIdentifier()}, at price (approx), {( placeOrderRequest.OrderType == OrderType.Market ? optionState.CurrentTick.LTP : placeOrderRequest.Price )}, {optionState.CurrentTick.Timestamp}";
         await LogAsync( LogLevel.Information, log );

         // DIAG::
         Console.WriteLine( log );

         // Long orders
         // Place long order for PE
         optionStateLong.CurrentOrderId = Guid.NewGuid().ToString();

         placeOrderRequest = new PlaceOrderRequest()
         {
            OrderType = OrderType.Market,
            Price = optionStateLong.CurrentTick.LTP,
            Quantity = qty,
            Symbol = _symbolOptionsLong,
            Timestamp = optionStateLong.CurrentTick.Timestamp,
            TradeExchange = ( LaunchMode == StrategyLaunchMode.Backtesting || LaunchMode == StrategyLaunchMode.PaperTrading ) ? TradeExchange.PAPER : TradeExchange.NSE,
            TriggerPrice = optionStateLong.CurrentTick.LTP,
            OrderDirection = OrderDirection.Sell,
            Tag = optionStateLong.CurrentOrderId,
            OrderProductType = OrderProductType.Normal,
            SlippageType = SlippageType.TIME,
            Slippage = 1000
         };

         await PlaceOrderAsync( placeOrderRequest );

         // Store our state
         await SetDataAsync( "state_map", _stateMap );

         // Log the buy initiation
         log = $"{optionStateLong.CurrentTick.Timestamp}, Spot, {idxState.CurrentTick.LTP}, Placed, sell, order for, {qty}, units of, {_symbolOptionsLong.GetInstrumentIdentifier()}, at price (approx), {( placeOrderRequest.OrderType == OrderType.Market ? optionStateLong.CurrentTick.LTP : placeOrderRequest.Price )}, {optionStateLong.CurrentTick.Timestamp}";
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

         // Run backtest
         return await base.BacktestAsync( backtestRequest );
      }

      public async override Task<Dictionary<string, object>> GetStatsAsync( TickData tickData )
      {
         if ( ( _symbolOptionsShort == null ) ||
            ( _symbolOptionsLong == null ) )
            return new Dictionary<string, object>();

         try
         {
            var statsMap = new Dictionary<string, object>();
            var shortOptionSymbolState = _stateMap[_symbolOptionsShort];
            var longOptionSymbolState = _stateMap[_symbolOptionsLong];

            var symbolTicks = new List<KeyValuePair<Symbol, TickData>>();

            if ( shortOptionSymbolState.CurrentTick != null )
               symbolTicks.Add( new KeyValuePair<Symbol, TickData>( _symbolOptionsShort, shortOptionSymbolState.CurrentTick ) );

            if ( longOptionSymbolState.CurrentTick != null )
               symbolTicks.Add( new KeyValuePair<Symbol, TickData>( _symbolOptionsLong, longOptionSymbolState.CurrentTick ) );

            var summary = await GetStrategyRunSummaryAsync( Capital, symbolTicks, StatsType.ExpiryPairedOptions, 4 );

            statsMap.Add( "Total Capital", summary.Capital );
            statsMap.Add( "Total PL", summary.PL );
            statsMap.Add( "Total Max Draw Down", summary.MaxDrawdown );

            if ( summary != null && summary.SymbolStats != null )
            {
               if ( summary.SymbolStats != null )
               {
                  foreach ( var kvp in summary.SymbolStats )
                  {
                     foreach ( var kvpSym in kvp.Value )
                     {
                        statsMap.Add( $"{kvp.Key}-{kvpSym.Key}", kvpSym.Value );
                     }
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