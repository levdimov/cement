using System;
using System.IO;
using System.Text;
using System.Threading;
using CliWrap;
using CliWrap.Exceptions;
using Common.Logging;
using Microsoft.Extensions.Logging;

namespace Common
{
    public class ShellRunner
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
        public static string LastOutput;

        private readonly StringBuilder processOutput = new StringBuilder();
        public string Output => processOutput.ToString();

        private readonly StringBuilder processErrors = new StringBuilder();
        public string Errors => processErrors.ToString();

        public bool HasTimeout;

        private readonly ILogger log;

        public ShellRunner(ILogger log = null)
        {
            log ??= LogManager.GetLogger(typeof(ModuleGetter));

            this.log = log;
        }

        private void BeforeRun()
        {
            processOutput.Clear();
            processErrors.Clear();
            HasTimeout = false;
        }

        public Action<string> OnOutputChange = _ => {};
        public Action<string> OnErrorsChange = _ => {};

        private int RunThreeTimes(string commandWithArguments, string workingDirectory, TimeSpan timeout, RetryStrategy retryStrategy = RetryStrategy.IfTimeout)
        {
            var exitCode = RunOnce(commandWithArguments, workingDirectory, timeout);
            var times = 2;

            while (times-- > 0 && NeedRunAgain(retryStrategy, exitCode))
            {
                if (HasTimeout)
                {
                    timeout = TimeoutHelper.IncreaseTimeout(timeout);
                }
                exitCode = RunOnce(commandWithArguments, workingDirectory, timeout);
                log.LogDebug($"EXECUTED {commandWithArguments} in {workingDirectory} with exitCode {exitCode} and retryStrategy {retryStrategy}");
            }
            return exitCode;
        }

        private bool NeedRunAgain(RetryStrategy retryStrategy, int exitCode)
        {
            if (retryStrategy == RetryStrategy.IfTimeout && HasTimeout)
                return true;
            if (retryStrategy == RetryStrategy.IfTimeoutOrFailed && (exitCode != 0 || HasTimeout))
                return true;
            return false;
        }

        public int RunOnce(string commandWithArguments, string workingDirectory, TimeSpan timeout)
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(timeout);

            string BuildArguments(string command)
            {
                var shellArgs = Helper.OsIsUnix() ? " -lc " : " /D /C ";
                return $"{shellArgs} \"{command}\"";
            }

            var cli = Cli
                .Wrap(Helper.OsIsUnix() ? "/bin/bash" : "cmd")
                .WithArguments(BuildArguments(commandWithArguments))
                .WithStandardOutputPipe(
                    PipeTarget.Merge(
                        PipeTarget.ToStringBuilder(processOutput),
                        PipeTarget.ToDelegate(OnOutputChange)
                    )
                )
                .WithStandardErrorPipe(
                    PipeTarget.Merge(
                        PipeTarget.ToStringBuilder(processErrors),
                        PipeTarget.ToDelegate(OnErrorsChange)
                    )
                )
                .WithWorkingDirectory(workingDirectory);

            BeforeRun();

            try
            {
                CommandTask<CommandResult> commandTask;
                CommandResult commandResult;
                try
                {
                    commandTask = cli.ExecuteAsync(cancellationTokenSource.Token);
                    commandResult = commandTask.GetAwaiter().GetResult();

                    LastOutput = Output;
                    var exitCode = commandResult.ExitCode;
                    log.LogInformation($"EXECUTED {commandWithArguments} in {workingDirectory} in {commandResult.RunTime.TotalMilliseconds}ms with exitCode {exitCode}");

                    return exitCode;
                }
                catch (CommandExecutionException)
                {
                    return 1;
                }
                catch (OperationCanceledException)
                {
                    HasTimeout = true;
                    var message = string.Format("Running timeout at {2} for command {0} in {1}", commandWithArguments, workingDirectory, timeout);
                    processErrors.AppendLine(message);

                    throw new TimeoutException(message);
                }
            }
            catch (CementException e)
            {
                if (e is TimeoutException)
                {
                    if (!commandWithArguments.Equals("git ls-remote --heads"))
                        ConsoleWriter.WriteWarning(e.Message);
                    log?.LogWarning(e.Message);
                }
                else
                {
                    ConsoleWriter.WriteError(e.Message);
                    log?.LogError(e.Message);
                }
                return -1;
            }
        }

        public int Run(string commandWithArguments)
        {
            return Run(commandWithArguments, DefaultTimeout);
        }

        public int Run(string commandWithArguments, TimeSpan timeout, RetryStrategy retryStrategy = RetryStrategy.IfTimeout)
        {
            return RunThreeTimes(commandWithArguments, Directory.GetCurrentDirectory(), timeout, retryStrategy);
        }

        public int RunInDirectory(string path, string commandWithArguments)
        {
            return RunInDirectory(path, commandWithArguments, DefaultTimeout);
        }

        public int RunInDirectory(string path, string commandWithArguments, TimeSpan timeout, RetryStrategy retryStrategy = RetryStrategy.IfTimeout)
        {
            return RunThreeTimes(commandWithArguments, path, timeout, retryStrategy);
        }
    }

    public enum RetryStrategy
    {
        None,
        IfTimeout,
        IfTimeoutOrFailed
    }

    public static class TimeoutHelper
    {
        private static readonly TimeSpan SmallTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan BigTimeout = TimeSpan.FromMinutes(10);
        private const int TimesForUseBigDefault = 1;

        private static int badTimes;

        public static TimeSpan IncreaseTimeout(TimeSpan was)
        {
            badTimes++;
            return was < BigTimeout ? BigTimeout : was;
        }

        public static TimeSpan GetStartTimeout()
        {
            if (badTimes > TimesForUseBigDefault)
                return BigTimeout;
            return SmallTimeout;
        }
    }
}