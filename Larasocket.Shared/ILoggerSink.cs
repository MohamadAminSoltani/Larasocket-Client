using System;
using System.Collections.Generic;
using System.Text;

namespace Larasocket.Shared
{
    internal interface ILoggerSink
    {
        /// <summary>
        /// Implement this method to log messages using your current infrastructure.
        /// </summary>
        /// <param name="level">the log level of the message.</param>
        /// <param name="message">the actual message.</param>
        void LogEvent(LogLevel level, string message);
    }
}
