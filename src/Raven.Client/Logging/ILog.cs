using System;

namespace Raven.Client.Logging
{
    internal interface ILog
    {
        bool IsInfoEnabled { get; }

        bool IsDebugEnabled { get; }

        bool IsWarnEnabled { get; }

        void Log(LogLevel logLevel, Func<string> messageFunc);

        void Log<TException>(LogLevel logLevel, Func<string> messageFunc, TException exception) where TException : Exception;
        bool ShouldLog(LogLevel logLevel);
    }
}
