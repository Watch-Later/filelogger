﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Karambolo.Extensions.Logging.File.Test.Helpers;
using Karambolo.Extensions.Logging.File.Test.Mocks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Karambolo.Extensions.Logging.File.Test
{
    public class LoggingTest
    {
        private async Task LoggingToMemoryWithoutDICore(LogFileAccessMode accessMode)
        {
            const string logsDirName = "Logs";

            var fileProvider = new MemoryFileProvider();

            var filterOptions = new LoggerFilterOptions { MinLevel = LogLevel.Trace };

            var options = new FileLoggerOptions
            {
                FileAppender = new MemoryFileAppender(fileProvider),
                BasePath = logsDirName,
                FileAccessMode = accessMode,
                FileEncoding = Encoding.UTF8,
                MaxQueueSize = 100,
                DateFormat = "yyMMdd",
                CounterFormat = "000",
                MaxFileSize = 10,
                Files = new[]
                {
                    new LogFileOptions
                    {
                        Path = "<date>/<date:MM>/logger.log",
                        DateFormat = "yyyy",
                        MinLevel = new Dictionary<string, LogLevel>
                        {
                            ["Karambolo.Extensions.Logging.File"] = LogLevel.None,
                            [LogFileOptions.DefaultCategoryName] = LogLevel.Information,
                        }
                    },
                    new LogFileOptions
                    {
                        Path = "test-<date>-<counter>.log",
                        MinLevel = new Dictionary<string, LogLevel>
                        {
                            ["Karambolo.Extensions.Logging.File"] = LogLevel.Information,
                            [LogFileOptions.DefaultCategoryName] = LogLevel.None,
                        }

                    },
                },
                TextBuilder = new CustomLogEntryTextBuilder(),
                IncludeScopes = true,
            };

            var completeCts = new CancellationTokenSource();
            var context = new TestFileLoggerContext(completeCts.Token, completionTimeout: Timeout.InfiniteTimeSpan);
            context.SetTimestamp(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var ex = new Exception();

            var provider = new FileLoggerProvider(context, Options.Create(options));
            try
            {
                using (var loggerFactory = new LoggerFactory(new[] { provider }, filterOptions))
                {
                    ILogger<LoggingTest> logger1 = loggerFactory.CreateLogger<LoggingTest>();

                    logger1.LogInformation("This is a nice logger.");
                    using (logger1.BeginScope("SCOPE"))
                    {
                        logger1.LogWarning(1, "This is a smart logger.");
                        logger1.LogTrace("This won't make it.");

                        using (logger1.BeginScope("NESTED SCOPE"))
                        {
                            ILogger logger2 = loggerFactory.CreateLogger("X");
                            logger2.LogWarning("Some warning.");
                            logger2.LogError(0, ex, "Some failure!");
                        }
                    }
                }
            }
            finally
            {
#if NETCOREAPP3_0
                await provider.DisposeAsync();
#else
                await Task.CompletedTask;
                provider.Dispose();
#endif
            }

            Assert.True(provider.Completion.IsCompleted);

            var logFile = (MemoryFileInfo)fileProvider.GetFileInfo($"{logsDirName}/test-{context.GetTimestamp().ToLocalTime():yyMMdd}-000.log");
            Assert.True(logFile.Exists && !logFile.IsDirectory);

            var lines = logFile.ReadAllText(out Encoding encoding).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            Assert.Equal(Encoding.UTF8, encoding);
            Assert.Equal(new[]
            {
                $"[info]: {typeof(LoggingTest)}[0] @ {context.GetTimestamp().ToLocalTime():o}",
                $"        This is a nice logger.",
                ""
            }, lines);

            logFile = (MemoryFileInfo)fileProvider.GetFileInfo($"{logsDirName}/test-{context.GetTimestamp().ToLocalTime():yyMMdd}-001.log");
            Assert.True(logFile.Exists && !logFile.IsDirectory);

            lines = logFile.ReadAllText(out encoding).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            Assert.Equal(Encoding.UTF8, encoding);
            Assert.Equal(new[]
            {
                $"[warn]: {typeof(LoggingTest)}[1] @ {context.GetTimestamp().ToLocalTime():o}",
                $"        => SCOPE",
                $"        This is a smart logger.",
                ""
            }, lines);

            logFile = (MemoryFileInfo)fileProvider.GetFileInfo(
                $"{logsDirName}/{context.GetTimestamp().ToLocalTime():yyyy}/{context.GetTimestamp().ToLocalTime():MM}/logger.log");
            Assert.True(logFile.Exists && !logFile.IsDirectory);

            lines = logFile.ReadAllText(out encoding).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
            Assert.Equal(Encoding.UTF8, encoding);
            Assert.Equal(new[]
            {
                $"[warn]: X[0] @ {context.GetTimestamp().ToLocalTime():o}",
                $"        => SCOPE => NESTED SCOPE",
                $"        Some warning.",
                $"[fail]: X[0] @ {context.GetTimestamp().ToLocalTime():o}",
                $"        => SCOPE => NESTED SCOPE",
                $"        Some failure!",
            }
            .Concat(ex.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None))
            .Append(""), lines);
        }

        [Fact]
        public async Task LoggingToMemoryWithoutDI()
        {
            foreach (LogFileAccessMode accessMode in Enum.GetValues(typeof(LogFileAccessMode)))
                await LoggingToMemoryWithoutDICore(accessMode);
        }

        private async Task LoggingToPhysicalUsingDICore(LogFileAccessMode accessMode)
        {
            var logsDirName = Guid.NewGuid().ToString("D");

            var configData = new Dictionary<string, string>
            {
                [$"{nameof(FileLoggerOptions.BasePath)}"] = logsDirName,
                [$"{nameof(FileLoggerOptions.FileEncodingName)}"] = "UTF-16",
                [$"{nameof(FileLoggerOptions.DateFormat)}"] = "yyMMdd",
                [$"{nameof(FileLoggerOptions.FileAccessMode)}"] = accessMode.ToString(),
                [$"{nameof(FileLoggerOptions.Files)}:0:{nameof(LogFileOptions.Path)}"] = "logger-<date>.log",
                [$"{nameof(FileLoggerOptions.Files)}:0:{nameof(LogFileOptions.MinLevel)}:Karambolo.Extensions.Logging.File"] = LogLevel.None.ToString(),
                [$"{nameof(FileLoggerOptions.Files)}:0:{nameof(LogFileOptions.MinLevel)}:{LogFileOptions.DefaultCategoryName}"] = LogLevel.Information.ToString(),
                [$"{nameof(FileLoggerOptions.Files)}:1:{nameof(LogFileOptions.Path)}"] = "test-<date>.log",
                [$"{nameof(FileLoggerOptions.Files)}:1:{nameof(LogFileOptions.MinLevel)}:Karambolo.Extensions.Logging.File.Test"] = LogLevel.Information.ToString(),
                [$"{nameof(FileLoggerOptions.Files)}:1:{nameof(LogFileOptions.MinLevel)}:{LogFileOptions.DefaultCategoryName}"] = LogLevel.None.ToString(),
            };

            var cb = new ConfigurationBuilder();
            cb.AddInMemoryCollection(configData);
            IConfigurationRoot config = cb.Build();

            var tempPath = Path.Combine(Path.GetTempPath());
            var logPath = Path.Combine(tempPath, logsDirName);

            var fileProvider = new PhysicalFileProvider(tempPath);

            var cts = new CancellationTokenSource();
            var context = new TestFileLoggerContext(cts.Token, completionTimeout: Timeout.InfiniteTimeSpan);
            context.SetTimestamp(new DateTime(2017, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging(b => b.AddFile(context, o => o.FileAppender = new PhysicalFileAppender(fileProvider)));
            services.Configure<FileLoggerOptions>(config);

            if (Directory.Exists(logPath))
                Directory.Delete(logPath, recursive: true);

            try
            {
                var ex = new Exception();

                FileLoggerProvider[] providers;

                using (ServiceProvider sp = services.BuildServiceProvider())
                {
                    providers = context.GetProviders(sp).ToArray();
                    Assert.Equal(1, providers.Length);

                    ILogger<LoggingTest> logger1 = sp.GetService<ILogger<LoggingTest>>();

                    logger1.LogInformation("This is a nice logger.");
                    using (logger1.BeginScope("SCOPE"))
                    {
                        logger1.LogWarning(1, "This is a smart logger.");
                        logger1.LogTrace("This won't make it.");

                        using (logger1.BeginScope("NESTED SCOPE"))
                        {
                            ILoggerFactory loggerFactory = sp.GetService<ILoggerFactory>();
                            ILogger logger2 = loggerFactory.CreateLogger("X");
                            logger2.LogError(0, ex, "Some failure!");
                        }
                    }

                    cts.Cancel();

                    // ensuring that all entries are processed
                    await context.GetCompletion(sp);
                    Assert.True(providers.All(provider => provider.Completion.IsCompleted));
                }

                IFileInfo logFile = fileProvider.GetFileInfo($"{logsDirName}/test-{context.GetTimestamp().ToLocalTime():yyMMdd}.log");
                Assert.True(logFile.Exists && !logFile.IsDirectory);

                var lines = logFile.ReadAllText(out Encoding encoding).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                Assert.Equal(Encoding.Unicode, encoding);
                Assert.Equal(new[]
                {
                    $"info: {typeof(LoggingTest)}[0] @ {context.GetTimestamp().ToLocalTime():o}",
                    $"      This is a nice logger.",
                    $"warn: {typeof(LoggingTest)}[1] @ {context.GetTimestamp().ToLocalTime():o}",
                    $"      This is a smart logger.",
                    ""
                }, lines);

                logFile = fileProvider.GetFileInfo(
                    $"{logsDirName}/logger-{context.GetTimestamp().ToLocalTime():yyMMdd}.log");
                Assert.True(logFile.Exists && !logFile.IsDirectory);

                lines = logFile.ReadAllText(out encoding).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                Assert.Equal(Encoding.Unicode, encoding);
                Assert.Equal(new[]
                {
                    $"fail: X[0] @ {context.GetTimestamp().ToLocalTime():o}",
                    $"      Some failure!",
                }
                .Concat(ex.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.None))
                .Append(""), lines);
            }
            finally
            {
                Directory.Delete(logPath, recursive: true);
            }
        }

        [Fact]
        public async Task LoggingToPhysicalUsingDI()
        {
            foreach (LogFileAccessMode accessMode in Enum.GetValues(typeof(LogFileAccessMode)))
                await LoggingToPhysicalUsingDICore(accessMode);
        }
    }
}
