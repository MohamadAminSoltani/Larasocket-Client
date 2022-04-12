using System;

namespace Larasocket.Shared.Exceptions
{
    /// <summary>
    /// Custom exception related to LarasocketClient
    /// </summary>
    public class LarasocketException : Exception
    {
        /// <inheritdoc />
        public LarasocketException()
        {
        }

        /// <inheritdoc />
        public LarasocketException(string message)
            : base(message)
        {
        }

        /// <inheritdoc />
        public LarasocketException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
