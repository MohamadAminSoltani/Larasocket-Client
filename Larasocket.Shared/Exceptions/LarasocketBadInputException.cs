using System;

namespace Larasocket.Shared.Exceptions
{
    /// <summary>
    /// Custom exception that indicates bad user/client input
    /// </summary>
    public class LarasocketBadInputException : LarasocketException
    {
        /// <inheritdoc />
        public LarasocketBadInputException()
        {
        }

        /// <inheritdoc />
        public LarasocketBadInputException(string message) : base(message)
        {
        }

        /// <inheritdoc />
        public LarasocketBadInputException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
