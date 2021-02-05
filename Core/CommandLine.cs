// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Ngsa.Middleware;

namespace Ngsa.DataService
{
    /// <summary>
    /// Main application class
    /// </summary>
    public sealed partial class App
    {
        /// <summary>
        /// Combine env vars and command line values
        ///   env vars take precedent
        /// </summary>
        /// <param name="args">command line args</param>
        /// <returns>string[]</returns>
        public static List<string> CombineEnvVarsWithCommandLine(string[] args)
        {
            if (args == null)
            {
                args = Array.Empty<string>();
            }

            List<string> cmd = new List<string>(args);

            // add values from environment
            cmd.AddFromEnvironment("--data-service");
            cmd.AddFromEnvironment("--cache-duration");
            cmd.AddFromEnvironment("--in-memory");
            cmd.AddFromEnvironment("--no-cache");
            cmd.AddFromEnvironment("--perf-cache");
            cmd.AddFromEnvironment("--secrets-volume");
            cmd.AddFromEnvironment("--log-level", "-l");
            cmd.AddFromEnvironment("--zone");
            cmd.AddFromEnvironment("--region");
            cmd.AddFromEnvironment("--app-type");

            // was log level set
            IsLogLevelSet = cmd.Contains("--log-level") || cmd.Contains("-l");

            return cmd;
        }

        /// <summary>
        /// Build the RootCommand for parsing
        /// </summary>
        /// <returns>RootCommand</returns>
        public static RootCommand BuildRootCommand()
        {
            RootCommand root = new RootCommand
            {
                Name = "DataService",
                Description = "NGSA Data Service",
                TreatUnmatchedTokensAsErrors = true,
            };

            // add the options
            root.AddOption(new Option<AppType>(new string[] { "-a", "--app-type" }, () => AppType.DataService, "Application Type"));
            root.AddOption(new Option<string>(new string[] { "-s", "--data-service" }, () => "http://localhost:8080", "Data Service URL"));
            root.AddOption(new Option<int>(new string[] { "-d", "--cache-duration" }, () => 300, "Cache for duration (seconds)"));
            root.AddOption(new Option<bool>(new string[] { "-m", "--in-memory" }, "Use in-memory database"));
            root.AddOption(new Option<bool>(new string[] { "-n", "--no-cache" }, "Don't cache results"));
            root.AddOption(new Option<int>(new string[] { "-p", "--perf-cache" }, "Cache only when load exceeds value"));
            root.AddOption(new Option<string>(new string[] { "-v", "--secrets-volume" }, () => "secrets", "Secrets Volume Path"));
            root.AddOption(new Option<LogLevel>(new string[] { "-l", "--log-level" }, () => LogLevel.Warning, "Log Level"));
            root.AddOption(new Option<string>(new string[] { "-z", "--zone" }, () => string.Empty, "Zone for log"));
            root.AddOption(new Option<string>(new string[] { "-r", "--region" }, () => string.Empty, "Region for log"));
            root.AddOption(new Option<bool>(new string[] { "--dry-run" }, "Validates configuration"));

            // validate dependencies
            root.AddValidator(ValidateDependencies);

            return root;
        }

        /// <summary>
        /// Run the app
        /// </summary>
        /// <param name="config">command line config</param>
        /// <returns>status</returns>
        public static async Task<int> RunApp(Config config)
        {
            try
            {
                // assign command line values
                Config.AppType = config.AppType;
                Config.DataService = config.DataService;
                Config.LogLevel = config.LogLevel;
                Config.CacheDuration = config.CacheDuration;
                Config.InMemory = config.InMemory;
                Config.NoCache = config.NoCache;
                Config.PerfCache = config.PerfCache;
                Config.SecretsVolume = config.SecretsVolume;
                Config.Zone = string.IsNullOrEmpty(config.Zone) ? string.Empty : config.Zone.Trim();
                Config.Region = string.IsNullOrEmpty(config.Region) ? string.Empty : config.Region.Trim();

                Config.LogLevel = config.LogLevel <= LogLevel.Information ? LogLevel.Information : config.LogLevel;

                RequestLogger.Zone = Config.Zone;
                RequestLogger.Region = Config.Region;

                NgsaLogger.Zone = Config.Zone;
                NgsaLogger.Region = Config.Region;

                NgsaLog.Zone = Config.Zone;
                NgsaLog.Region = Config.Region;
                NgsaLog.LogLevel = Config.LogLevel;

                if (Config.AppType == AppType.WebAPI)
                {
                    Config.Port = 4120;
                    RequestLogger.CosmosName = string.Empty;
                    RequestLogger.DataService = Config.DataService.Replace("http://", string.Empty).Replace("https://", string.Empty);
                }
                else
                {
                    LoadSecrets(Config.SecretsVolume);

                    // load the cache
                    CacheDal = new DataAccessLayer.InMemoryDal();

                    // create the cosomos data access layer
                    if (Secrets.UseInMemoryDb)
                    {
                        CosmosDal = CacheDal;
                    }
                    else
                    {
                        CosmosDal = new DataAccessLayer.CosmosDal(new Uri(Secrets.CosmosServer), Secrets.CosmosKey, Secrets.CosmosDatabase, Secrets.CosmosCollection);
                    }

                    // set the logger info
                    RequestLogger.CosmosName = Secrets.CosmosServer;

                    // remove prefix and suffix
                    RequestLogger.CosmosName = RequestLogger.CosmosName.Replace("https://", string.Empty);
                    if (RequestLogger.CosmosName.IndexOf(".documents.azure.com") > 0)
                    {
                        RequestLogger.CosmosName = RequestLogger.CosmosName.Substring(0, RequestLogger.CosmosName.IndexOf(".documents.azure.com"));
                    }

                    RequestLogger.DataService = string.Empty;
                }

                // build the host
                host = BuildHost();

                if (host == null)
                {
                    return -1;
                }

                // display dry run message
                if (config.DryRun)
                {
                    return DoDryRun();
                }

                // setup ctl c handler
                ctCancel = SetupCtlCHandler();

                // log startup messages
                LogStartup();

                // start the webserver
                Task w = host.RunAsync();

                // start request count timer
                RequestLogger.StartCounterTime(5000, 1000);

                // this doesn't return except on ctl-c
                await w.ConfigureAwait(false);

                // if not cancelled, app exit -1
                return ctCancel.IsCancellationRequested ? 0 : -1;
            }
            catch (Exception ex)
            {
                // end app on error
                if (Logger != null)
                {
                    Logger.Method = nameof(RunApp);
                    Logger.LogError($"Exception: {ex.Message}");
                }

                return -1;
            }
        }

        // load secrets
        private static void LoadSecrets(string secretsVolume)
        {
            if (Config.InMemory)
            {
                Secrets = new Secrets
                {
                    UseInMemoryDb = true,
                    CosmosCollection = "movies",
                    CosmosDatabase = "imdb",
                    CosmosKey = "in-memory",
                    CosmosServer = "in-memory",
                };
            }
            else
            {
                Secrets = Secrets.GetSecretsFromVolume(secretsVolume);

                // set the Cosmos server name for logging
                CosmosName = Secrets.CosmosServer.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

                int ndx = CosmosName.IndexOf('.', StringComparison.OrdinalIgnoreCase);

                if (ndx > 0)
                {
                    CosmosName = CosmosName.Remove(ndx);
                }
            }
        }

        // validate combinations of parameters
        private static string ValidateDependencies(CommandResult result)
        {
            string msg = string.Empty;

            try
            {
                AppType appType = !(result.Children.FirstOrDefault(c => c.Symbol.Name == "app-type") is OptionResult appTypeRes) ? AppType.DataService : appTypeRes.GetValueOrDefault<AppType>();
                int? cacheDuration = !(result.Children.FirstOrDefault(c => c.Symbol.Name == "cache-duration") is OptionResult cacheDurationRes) ? null : cacheDurationRes.GetValueOrDefault<int?>();
                int? perfCache = !(result.Children.FirstOrDefault(c => c.Symbol.Name == "perf-cache") is OptionResult perfCacheRes) ? null : perfCacheRes.GetValueOrDefault<int?>();
                bool inMemory = result.Children.FirstOrDefault(c => c.Symbol.Name == "in-memory") is OptionResult inMemoryRes && inMemoryRes.GetValueOrDefault<bool>();
                bool noCache = result.Children.FirstOrDefault(c => c.Symbol.Name == "no-cache") is OptionResult noCacheRes && noCacheRes.GetValueOrDefault<bool>();
                string secrets = !(result.Children.FirstOrDefault(c => c.Symbol.Name == "secrets-volume") is OptionResult secretsRes) ? string.Empty : secretsRes.GetValueOrDefault<string>();

                // todo - validate --data-service

                // validate secrets volume
                if (string.IsNullOrWhiteSpace(secrets))
                {
                    msg += "--secrets-volume cannot be empty\n";
                }

                try
                {
                    // validate secrets-volume exists
                    if (!Directory.Exists(secrets))
                    {
                        msg += $"--secrets-volume ({secrets}) does not exist\n";
                    }
                }
                catch (Exception ex)
                {
                    msg += $"--secrets-volume exception: {ex.Message}\n";
                }

                // validate cache-duration
                if (cacheDuration < 1)
                {
                    msg += "--cache-duration must be > 0\n";
                }

                // invalid combination
                if (inMemory && noCache)
                {
                    msg += "--in-memory and --no-cache are exclusive\n";
                }

                if (perfCache != null)
                {
                    // validate perfCache > 0
                    if (perfCache < 1)
                    {
                        msg += "--perf-cache must be > 0\n";
                    }

                    // invalid combination
                    if (inMemory)
                    {
                        msg += "--perf-cache and --in-memory are exclusive\n";
                    }

                    // invalid combination
                    if (noCache)
                    {
                        msg += "--perf-cache and --no-cache are exclusive\n";
                    }
                }
            }
            catch
            {
                // system.commandline will catch and display parse exceptions
            }

            // return error message(s) or string.empty
            return msg;
        }

        // Display the dry run message
        private static int DoDryRun()
        {
            Console.WriteLine($"Version            {VersionExtension.Version}");
            Console.WriteLine($"Log Level          {Config.LogLevel}");
            Console.WriteLine($"Application Type   {Config.AppType}");
            Console.WriteLine($"In Memory          {Config.InMemory}");
            Console.WriteLine($"No Cache           {Config.NoCache}");
            Console.WriteLine($"Perf Cache         {Config.PerfCache}");
            Console.WriteLine($"Secrets Volume     {Secrets.Volume}");
            Console.WriteLine($"Use in memory DB   {Secrets.UseInMemoryDb}");
            Console.WriteLine($"Cosmos Server      {Secrets.CosmosServer}");
            Console.WriteLine($"Cosmos Database    {Secrets.CosmosDatabase}");
            Console.WriteLine($"Cosmos Collection  {Secrets.CosmosCollection}");
            Console.WriteLine($"Cosmos Key         Length({Secrets.CosmosKey.Length})");
            Console.WriteLine($"Zone               {Config.Zone}");
            Console.WriteLine($"Region             {Config.Region}");

            // always return 0 (success)
            return 0;
        }
    }
}
