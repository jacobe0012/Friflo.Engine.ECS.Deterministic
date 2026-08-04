namespace xpTURN.Klotho.Core
{
    /// <summary>
    /// Interface for sending commands from within the OnPollInput callback.
    /// Thin wrapper around Engine.InputCommand().
    /// </summary>
    public interface ICommandSender
    {
        /// <summary>
        /// Submit a command to the engine.
        ///
        /// <para><b>Ownership contract</b>: the caller transfers sole ownership of <paramref name="command"/>
        /// to the engine on entry. The caller MUST NOT retain or reuse the instance after this call returns —
        /// the engine may store it in InputBuffer, return it to CommandPool on cleanup, or hand it to the
        /// transport for serialization. Violating this contract risks pool poisoning.</para>
        /// </summary>
        void Send(ICommand command);
    }
}
