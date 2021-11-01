using System;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Configuration;

namespace Aditya.Strategy.CashFuturesArbitrage
{
   class Program
   {
      static async Task Main( string[] args )
      {
         // Get the url to connect from the arguments.
         // This will be local url when deployed on Algorum cloud.
         // For local debugging and testing, you can connect to the remote Algorum cloud, without deploying.
         var configBuilder = new ConfigurationBuilder().AddJsonFile( "appsettings.json" ).AddCommandLine( args ).AddEnvironmentVariables();
         var config = configBuilder.Build();

         string url = config.GetValue<string>( "url" );
         string apiKey = config.GetValue<string>( "apiKey" );
         string sid = config.GetValue<string>( "sid" );
         string bkApiKey = config.GetValue<string>( "bkApiKey" );
         string bkApiSecretKey = config.GetValue<string>( "bkApiSecretKey" );

         if ( string.IsNullOrWhiteSpace( url ) )
            url = "ws://localhost:5000/quant/engine/api/v1";

         url += $"?sid={sid}&apiKey={apiKey}";

         StrategyLaunchMode launchMode = config.GetValue<StrategyLaunchMode>( "launchMode" );

         // Create our strategy object
         var rsiStrategy = await CashFuturesArbitrageQuantStrategy.GetInstanceAsync( url, apiKey, launchMode, sid );

         // If we are started in backtestign mode, start the backtest
         if ( launchMode == StrategyLaunchMode.Backtesting )
         {
            DateTime startDate = DateTime.ParseExact( config.GetValue<string>( "startDate" ), "dd-MM-yyyy", null );
            DateTime endDate = DateTime.ParseExact( config.GetValue<string>( "endDate" ), "dd-MM-yyyy", null );

            await rsiStrategy.BacktestAsync( startDate, endDate, bkApiKey, bkApiSecretKey, 1 );
         }

         // Wait until our strategy is stopped
         rsiStrategy.Wait();
      }
   }
}
