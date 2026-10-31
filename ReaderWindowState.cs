using System;

namespace PdfReader
{
    /// <summary>Owns the reader's temporary window state; repeated close is harmless.</summary>
    internal sealed class ReaderWindowState
    {
        private readonly Func<bool> _getFullscreen;
        private readonly Action<bool> _setFullscreen;
        private readonly Action _selectMouse;
        private bool _active;
        private bool _previousFullscreen;

        internal ReaderWindowState(Func<bool> getFullscreen, Action<bool> setFullscreen, Action selectMouse)
        {
            _getFullscreen = getFullscreen;
            _setFullscreen = setFullscreen;
            _selectMouse = selectMouse;
        }

        internal void Begin()
        {
            if (_active) return;
            _previousFullscreen = _getFullscreen();
            _active = true;
        }

        internal void Show()
        {
            if (_active) _setFullscreen(true);
        }

        internal void End(bool selectMouse)
        {
            if (!_active) return;
            _active = false;
            try { _setFullscreen(_previousFullscreen); }
            finally { if (selectMouse) _selectMouse(); }
        }
    }
}
