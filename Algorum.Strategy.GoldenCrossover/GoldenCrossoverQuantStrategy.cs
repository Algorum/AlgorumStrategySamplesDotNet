using System;
using System.Collections.Generic;
using System.Linq;
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
         public CrossAbove RSICrossAboveObj;
      }

      class PLCalcState
      {
         public double SellVal;
         public double BuyVal;
         public double SellQty;
         public double BuyQty;
      }

      public const double Capital = 100000;
      private const double Leverage = 4; // 4x Leverage on Capital

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
      /// <param name="userId">User unique id</param>
      /// <returns>Instance of GoldenCrossoverQuantStrategy class</returns>
      public static async Task<GoldenCrossoverQuantStrategy> GetInstanceAsync(
         string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
      {
         var strategy = new GoldenCrossoverQuantStrategy( url, apiKey, launchMode, sid, userId );
         await strategy.InitializeAsync();
         return strategy;
      }

      private GoldenCrossoverQuantStrategy( string url, string apiKey, StrategyLaunchMode launchMode, string sid, string userId )
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
            _state.CrossAboveObj = new CrossAbove();
            _state.RSICrossAboveObj = new CrossAbove();
         }

         // Create our stock symbol object
         // For India users
         _symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "HDFCBANK" };

         // For USA users
         //_symbol = new Symbol() { SymbolType = SymbolType.Stock, Ticker = "SPY" };

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
            var log = $"{tickData.Timestamp}, {tickData.LTP}, ema50 {ema50}, ema200 {ema200}";
            await LogAsync( LogLevel.Debug, log );
            _state.LastTick = tickData;

            // DIAG::
            Console.WriteLine( log );
         }

         if ( ema50 > 0 && ema200 > 0 &&
            _state.CrossAboveObj.Evaluate( ema50, ema200 ) && ( !_state.Bought ) &&
            tickData.Timestamp.Hour > 9 && tickData.Timestamp.Hour < 15 &&
            ( string.IsNullOrWhiteSpace( _state.CurrentOrderId ) ) )
         {
            // Place buy order
            _state.CurrentOrderId = Guid.NewGuid().ToString();
            var qty = ( Capital / tickData.LTP ) * Leverage;

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
            if ( ( ( (
                  ( tickData.LTP - _state.CurrentOrder.AveragePrice >= _state.CurrentOrder.AveragePrice * 0.25 / 100 ) ||
                  ( _state.CurrentOrder.AveragePrice - tickData.LTP >= _state.CurrentOrder.AveragePrice * 0.5 / 100 ) ) &&
                  tickData.Timestamp.Hour >= 10 && tickData.Timestamp.Hour < 15 ) ) &&
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

      public override Dictionary<string, object> GetStats( TickData tickData )
      {
         var (pl, dd, statsMap) = GetStats( Capital, _state.Orders, new List<KeyValuePair<Symbol, TickData>>() {
            new KeyValuePair<Symbol, TickData>(_symbol,_state.CurrentTick)
         } );

         var stats = statsMap.FirstOrDefault( obj => obj.Key.Equals( _symbol ) ).Value;

         if ( stats == null )
            stats = new Dictionary<string, object>();

         return stats;
      }

      private static (double totalPL, double totalDrawdown, List<KeyValuePair<Symbol, Dictionary<string, object>>> symbolStats) GetStats(
                        double capital,
                        List<Order> orders,
                        List<KeyValuePair<Symbol, TickData>> symbolLastTicks )
      {
         var symbolStats = new List<KeyValuePair<Symbol, Dictionary<string, object>>>();

         var symbolOrders = from order in orders
                            group order by new { Symbol = order.Symbol } into d
                            select new { Symbol = d.Key.Symbol, Orders = d.ToList() };

         Dictionary<Symbol, PLCalcState> stateCalcMap = new Dictionary<Symbol, PLCalcState>();
         double totalPL = 0;
         double totalDrawdown = 0;
         double totalMaxPL = 0;
         double totalMinPL = 0;

         foreach ( var symbolOrder in symbolOrders )
         {
            var statsMap = new Dictionary<string, object>();
            stateCalcMap.Add( symbolOrder.Symbol, new PLCalcState() );

            statsMap["Capital"] = capital;
            statsMap["Order Count"] = symbolOrder.Orders.Count;

            var groupedOrders = from order in symbolOrder.Orders
                                group order by new { Month = order.OrderTimestamp.Value.Month, Year = order.OrderTimestamp.Value.Year } into d
                                select new { Date = $"{d.Key.Month:00}-{d.Key.Year:0000}", Orders = d.ToList() };

            double pl = 0;
            double drawdown = 0;
            double maxPL = 0;
            double minPL = 0;

            foreach ( var orderGroup in groupedOrders )
            {
               foreach ( var order in orderGroup.Orders )
               {
                  try
                  {
                     var orderStatePLCalc = stateCalcMap[order.Symbol];

                     if ( ( order.Status == OrderStatus.Completed ) && ( order.OrderDirection == OrderDirection.Buy ) )
                     {
                        orderStatePLCalc.BuyVal += order.FilledQuantity * order.AveragePrice;
                        orderStatePLCalc.BuyQty += order.FilledQuantity;
                     }

                     if ( ( order.Status == OrderStatus.Completed ) && ( order.OrderDirection == OrderDirection.Sell ) )
                     {
                        orderStatePLCalc.SellVal += order.FilledQuantity * order.AveragePrice;
                        orderStatePLCalc.SellQty += order.FilledQuantity;
                     }
                  }
                  catch ( Exception ex )
                  {
                     Console.WriteLine( ex );
                  }
               }

               var orderStatePLCalcOuter = stateCalcMap[symbolOrder.Symbol];
               double ltp = 0;

               if ( symbolLastTicks != null && symbolLastTicks.Count > 0 )
                  ltp = symbolLastTicks.FirstOrDefault( obj => obj.Key.Equals( symbolOrder.Symbol ) ).Value.LTP;
               else
                  ltp = orderGroup.Orders.Last().LastTick.LTP;

               if ( orderStatePLCalcOuter.SellQty < orderStatePLCalcOuter.BuyQty )
                  orderStatePLCalcOuter.SellVal += ( orderStatePLCalcOuter.BuyQty - orderStatePLCalcOuter.SellQty ) * ltp;

               if ( orderStatePLCalcOuter.BuyQty < orderStatePLCalcOuter.SellQty )
                  orderStatePLCalcOuter.BuyVal += ( orderStatePLCalcOuter.SellQty - orderStatePLCalcOuter.BuyQty ) * ltp;

               double plMonth = 0;

               foreach ( var orderSymbol in orderGroup.Orders.Select( obj => obj.Symbol ).Distinct() )
               {
                  var orderStatePLCalcObj = stateCalcMap[orderSymbol];
                  plMonth += orderStatePLCalcObj.SellVal - orderStatePLCalcObj.BuyVal;
               }

               statsMap[$"PL-{orderGroup.Date}"] = plMonth;
               pl += plMonth;

               maxPL = Math.Max( pl, maxPL );

               if ( pl < 0 )
                  minPL = Math.Min( pl, minPL );
            }

            if ( minPL == 0 )
               drawdown = 0;
            else
               drawdown = ( Math.Abs( minPL ) / ( maxPL + capital ) ) * 100;

            statsMap["PL"] = pl;
            statsMap["DrawDown"] = drawdown;

            symbolStats.Add( new KeyValuePair<Symbol, Dictionary<string, object>>( symbolOrder.Symbol, statsMap ) );
            totalPL += pl;

            totalMaxPL = Math.Max( totalPL, totalMaxPL );

            if ( totalPL < 0 )
               totalMinPL = Math.Min( totalPL, totalMinPL );
         }

         if ( totalMinPL == 0 )
            totalDrawdown = 0;
         else
            totalDrawdown = ( Math.Abs( totalMinPL ) / ( totalMaxPL + capital ) ) * 100;

         return (totalPL, totalDrawdown, symbolStats);
      }
   }
}