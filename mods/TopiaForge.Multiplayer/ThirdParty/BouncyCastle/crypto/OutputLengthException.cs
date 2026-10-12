using System;
using System.Runtime.Serialization;

namespace TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Crypto
{
    [Serializable]
    internal class OutputLengthException
        : DataLengthException
    {
        public OutputLengthException()
            : base()
        {
        }

        public OutputLengthException(string message)
            : base(message)
        {
        }

        public OutputLengthException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        protected OutputLengthException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
