using System;
using Buckminster.Ffi;

namespace Buckminster;

// C#'s one point of access for logging: every record round-trips through the Rust core (buck_log), so C# and Rust records share one buffer, one ordering, one set of thresholds, one stderr echo, and one delivery path to the engine's log sink. A record emitted here is delivered to the log sink synchronously, by this very call's exit-drain -- causal stacks preserved. Logging is process-scoped, hence static. Calling before any engine has been created is a loud error, not silence.
public static class Log
{
    public static void Error(string message)
    {
        Emit(LogLevel.Error, message);
    }

    public static void Warn(string message)
    {
        Emit(LogLevel.Warn, message);
    }

    public static void Info(string message)
    {
        Emit(LogLevel.Info, message);
    }

    public static void Debug(string message)
    {
        Emit(LogLevel.Debug, message);
    }

    public static void Trace(string message)
    {
        Emit(LogLevel.Trace, message);
    }

    // The "just log this exception" call (planefarer Dbg.Ex lineage): Error level, full ToString -- type, message, stack trace, inner exceptions.
    public static void Ex(Exception exception)
    {
        Emit(LogLevel.Error, exception.ToString());
    }

    private static void Emit(LogLevel level, string message)
    {
        // FfiCall rethrows a throwing log sink's exception right here -- at the causal call site, which is the point of exit-drain delivery.
        FfiCall.ThrowOnError(NativeMethods.EmitLog((int)level, message), "Log emit");
    }
}
