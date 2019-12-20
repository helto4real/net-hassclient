﻿using Microsoft.Extensions.Logging;
using System;

class LoggerMock : ILogger
{
    public bool LoggedError { get; internal set; } = false;
    public bool LoggedTrace { get; internal set; } = false;
    public bool LoggedDebug { get; internal set; } = false;

    public IDisposable BeginScope<TState>(TState state) => throw new NotImplementedException();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (logLevel == LogLevel.Trace)
            LoggedTrace = true;
        if (logLevel == LogLevel.Debug)
            LoggedDebug = true;
        if (logLevel == LogLevel.Error)
            LoggedError = true;
    }
}
class LoggerFactoryMock : ILoggerFactory
{
    LoggerMock _logger = null;

    public bool LoggedError => _logger.LoggedError;
    public bool LoggedTrace => _logger.LoggedTrace;

    public bool LoggedDebug => _logger.LoggedDebug;


    public void AddProvider(ILoggerProvider provider) => throw new NotImplementedException();
    public ILogger CreateLogger(string categoryName)
    {
        _logger = new LoggerMock();
        return _logger;
    }
    public void Dispose() => throw new NotImplementedException();
}
