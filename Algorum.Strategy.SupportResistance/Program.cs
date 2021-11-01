using System;
using System.Threading.Tasks;
using Algorum.Quant.Types;
using Microsoft.Extensions.Configuration;

namespace Algorum.Strategy.SupportResistance
{
   class Program
   {
      static async Task Main( string[] args )
      {
         // Get the url to connect from the arguments.
         // This will be local url when deployed on Algorum cloud.
         // For local debugging and testing, you can connect to the remote Algorum cloud, without deploying.
         var configBuilder = new ConfigurationBuilder().AddJsonFile( "appsettings.json" ).AddEnvironmentVariables();
         var config = configBuilder.Build();

         string url = config.GetValue<string>( "url" );
         string apiKey = config.GetValue<string>( "apiKey" );
         string sid = config.GetValue<string>( "sid" );
         string bkApiKey = config.GetValue<string>( "bkApiKey" );
         string bkApiSecretKey = config.GetValue<string>( "bkApiSecretKey" );
         string clientCode = config.GetValue<string>( "clientCode" );
         string password = config.GetValue<string>( "password" );
         string twoFactorAuth = config.GetValue<string>( "twoFactorAuth" );
         BrokeragePlatform brokeragePlatform = config.GetValue<BrokeragePlatform>( "brokeragePlatform" );
         int samplingTime = config.GetValue<int>( "samplingTime" );
         StrategyLaunchMode launchMode = config.GetValue<StrategyLaunchMode>( "launchMode" );

         url += $"?sid={sid}&apiKey={apiKey}&launchMode={launchMode}";

         // Create our strategy object
         var strategy = await SupportResistanceStrategy.GetInstanceAsync( url, apiKey, launchMode, sid );

         // If we are started in backtestign mode, start the backtest
         if ( launchMode == StrategyLaunchMode.Backtesting )
         {
            DateTime startDate = DateTime.ParseExact( config.GetValue<string>( "startDate" ), "dd-MM-yyyy", null );
            DateTime endDate = DateTime.ParseExact( config.GetValue<string>( "endDate" ), "dd-MM-yyyy", null );

            await strategy.BacktestAsync( new BacktestRequest()
            {
               StartDate = startDate,
               EndDate = endDate,
               ApiKey = bkApiKey,
               ApiSecretKey = bkApiSecretKey,
               SamplingTimeInSeconds = samplingTime,
               BrokeragePlatform = brokeragePlatform
            } );
         }
         else
         {
            // Else, start trading in live or paper trading mode as per the given launchMode
            await strategy.StartTradingAsync( new TradingRequest()
            {
               ApiKey = bkApiKey,
               ApiSecretKey = bkApiSecretKey,
               ClientCode = clientCode,
               Password = password,
               TwoFactorAuth = twoFactorAuth,
               SamplingTimeInSeconds = samplingTime,
               BrokeragePlatform = brokeragePlatform
            } );
         }

         // Wait until our strategy is stopped
         strategy.Wait();
      }
   }
}
