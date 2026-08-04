#if DEBUG
using xpTURN.Klotho.Logging;

namespace xpTURN.Klotho.Core
{
    public partial class KlothoEngine
    {
        // SyncTest mode
        private SyncTestRunner _syncTestRunner;
        private bool _syncTestEnabled;

        #region SyncTest Methods

        /// <summary>
        /// Enables SyncTest mode (DEBUG/editor only).
        /// Each tick: forward execution → rollback by checkDistance ticks → re-simulation → hash comparison.
        /// </summary>
        public void EnableSyncTest(int checkDistance = 5)
        {
            _syncTestRunner = new SyncTestRunner();
            _syncTestRunner.Initialize(_simulation, checkDistance);
            _syncTestRunner.OnSyncError += HandleSyncTestError;
            _syncTestRunner.OnSyncTestDisabled += HandleSyncTestDisabled;
            _syncTestEnabled = true;
        }

        /// <summary>
        /// Disables SyncTest mode.
        /// </summary>
        public void DisableSyncTest()
        {
            if (_syncTestRunner != null)
            {
                _syncTestRunner.OnSyncError -= HandleSyncTestError;
                _syncTestRunner.OnSyncTestDisabled -= HandleSyncTestDisabled;
                _syncTestRunner = null;
            }
            _syncTestEnabled = false;
        }

        /// <summary>
        /// Whether SyncTest mode is currently enabled.
        /// </summary>
        public bool IsSyncTestEnabled => _syncTestEnabled;

        /// <summary>
        /// SyncTest runner instance (null when disabled).
        /// </summary>
        public ISyncTestRunner SyncTestRunner => _syncTestRunner;

        private void HandleSyncTestError(SyncTestFailure failure)
        {
            _logger?.KError($"[KlothoEngine][SyncTest] Failed tick={failure.Tick} distance={failure.RollbackDistance} entities={failure.EntityCount} expected=0x{failure.ExpectedHash:X16} actual=0x{failure.ActualHash:X16}");
        }

        private void HandleSyncTestDisabled()
        {
            _logger?.KError($"[KlothoEngine][SyncTest] auto-disabled: {_syncTestRunner?.ConsecutiveFailLimit} consecutive failed checks — persistent non-determinism, further checks would repeat the same signal.");
            DisableSyncTest();
        }

        #endregion
    }
}
#endif
