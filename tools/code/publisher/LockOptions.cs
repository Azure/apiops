using AsyncKeyedLock;

namespace publisher
{
    internal static class LockOptions
    {
        private readonly static AsyncKeyedLockOptions defaultOptions = new()
        {
            PoolSize = 20,
            PoolInitialFill = 1
        };

        internal static AsyncKeyedLockOptions Default => defaultOptions;
    }
}