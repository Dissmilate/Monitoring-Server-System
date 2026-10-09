namespace ServerMonitor.Application.Abstractions;

public sealed class ConcurrencyConflictException(string message, Exception? innerException = null)
    : Exception(message, innerException);
