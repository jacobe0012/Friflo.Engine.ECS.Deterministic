namespace xpTURN.Klotho.Core
{
    public static class KlothoModeStrategy
    {
        public static IKlothoModeStrategy Resolve(NetworkMode mode) => mode switch
        {
            NetworkMode.P2P          => P2PModeStrategy.Instance,
            NetworkMode.ServerDriven => ServerDrivenModeStrategy.Instance,
            _                        => throw new System.ArgumentOutOfRangeException(nameof(mode))
        };

        public static IKlothoModeStrategy Resolve(ISimulationConfig simConfig)
        {
            // Silent fallback to P2P on null would mask a configuration bug.
            if (simConfig == null) throw new System.ArgumentNullException(nameof(simConfig));
            return Resolve(simConfig.Mode);
        }
    }
}
