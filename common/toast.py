"""Windows toast notifications from the AppUserModelID `PowerManager` (registered by install.ps1 under
HKCU\\Software\\Classes\\AppUserModelId\\PowerManager, so the toast shows that name and ui/icon.png).

Sent through Windows PowerShell's WinRT bridge: one short process per notification (only on wake and
on the rare events the agent reports), no extra Python packages.
"""
import os
import subprocess
from xml.sax.saxutils import escape

from .util import AUMID, ICON_PNG, NO_WINDOW, POWERSHELL

_PS = r'''
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] > $null
$x = New-Object Windows.Data.Xml.Dom.XmlDocument
$x.LoadXml($env:PM_TOAST_XML)
$t = [Windows.UI.Notifications.ToastNotification]::new($x)
$t.Tag = $env:PM_TOAST_TAG
$t.Group = 'PowerManager'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($env:PM_TOAST_APP).Show($t)
'''


def build_xml(title, lines, attribution=None):
    logo = ''
    if ICON_PNG.exists():
        logo = f'<image placement="appLogoOverride" hint-crop="circle" src="{escape(ICON_PNG.as_uri())}"/>'
    body = ''.join(f'<text>{escape(s)}</text>' for s in lines[:2])
    attr = f'<text placement="attribution">{escape(attribution)}</text>' if attribution else ''
    return (f'<toast><visual><binding template="ToastGeneric">{logo}'
            f'<text hint-maxLines="1">{escape(title)}</text>{body}{attr}</binding></visual>'
            f'<audio silent="true"/></toast>')


def show(title, lines=(), attribution=None, tag='status'):
    """Show a toast; a newer toast with the same tag replaces the older one. Never raises."""
    env = {**os.environ, 'PM_TOAST_XML': build_xml(title, list(lines), attribution),
           'PM_TOAST_TAG': tag, 'PM_TOAST_APP': AUMID}
    try:
        r = subprocess.run([POWERSHELL, '-NoProfile', '-NonInteractive', '-Command', _PS], env=env,
                           capture_output=True, text=True, timeout=30, creationflags=NO_WINDOW)
        return r.returncode == 0
    except Exception:
        return False
