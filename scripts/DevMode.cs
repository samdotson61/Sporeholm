using System;

namespace Sporeholm
{
    // v0.4.32 — RimWorld-style Developer Mode global toggle.
    //
    // When IsEnabled is true, the `DevPanel` floating UI is rendered (F12
    // toggles its visibility) and HUD / context menus may surface
    // additional debug actions. When false, dev controls are hidden and
    // gated behind this flag — no accidental player-facing access.
    //
    // Persistence lives in `SettingsPanel`'s user://settings.cfg under
    // the `[gameplay]` section as `developer_mode`. Default off.
    //
    // Thread model: read/write on main thread only. The Changed event
    // fires immediately when the setting flips so any subscribed panel
    // can show / hide itself.
    public static class DevMode
    {
        private static bool _enabled = false;

        public static event Action? Changed;

        public static bool IsEnabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                Changed?.Invoke();
            }
        }
    }
}
