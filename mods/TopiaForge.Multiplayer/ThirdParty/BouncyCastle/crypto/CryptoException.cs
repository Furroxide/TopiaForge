using System;
using System.Runtime.Serialization;

namespace TopiaForge.Multiplayer.ThirdParty.BouncyCastle.Crypto
{
    [Serializable]
    internal class CryptoException
        : Exception
    {
        public CryptoException()
            : base()
        {
        }

        public CryptoException(string message)
            : base(message)
        {
        }

        public CryptoException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        protected CryptoException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}
