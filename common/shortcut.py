"""Set System.AppUserModel.ID on a .lnk, so Windows shows toasts sent under that ID.

Desktop (unpackaged) apps only get their toasts displayed when a Start menu shortcut carries the
AppUserModelID (2026-11-22: the registry entry alone was accepted but nothing was shown).
    python shortcut.py <path.lnk> <AppUserModelID>     (install.ps1 runs this)
"""
import ctypes
import ctypes.wintypes as wt
import sys

import comtypes
import comtypes.client as cc
from comtypes import COMMETHOD, GUID, HRESULT, IUnknown
from comtypes.persist import IPersistFile
from comtypes.shelllink import ShellLink


class PROPERTYKEY(ctypes.Structure):
    _fields_ = [('fmtid', GUID), ('pid', wt.DWORD)]


class PROPVARIANT(ctypes.Structure):
    _fields_ = [('vt', ctypes.c_ushort), ('r1', ctypes.c_ushort), ('r2', ctypes.c_ushort), ('r3', ctypes.c_ushort),
                ('pwsz', ctypes.c_wchar_p), ('pad', ctypes.c_void_p)]


class IPropertyStore(IUnknown):
    _iid_ = GUID('{886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99}')
    _methods_ = [
        COMMETHOD([], HRESULT, 'GetCount', (['out'], ctypes.POINTER(wt.DWORD), 'n')),
        COMMETHOD([], HRESULT, 'GetAt', (['in'], wt.DWORD, 'i'), (['out'], ctypes.POINTER(PROPERTYKEY), 'k')),
        COMMETHOD([], HRESULT, 'GetValue', (['in'], ctypes.POINTER(PROPERTYKEY), 'k'),
                  (['out'], ctypes.POINTER(PROPVARIANT), 'v')),
        COMMETHOD([], HRESULT, 'SetValue', (['in'], ctypes.POINTER(PROPERTYKEY), 'k'),
                  (['in'], ctypes.POINTER(PROPVARIANT), 'v')),
        COMMETHOD([], HRESULT, 'Commit'),
    ]


PKEY_AppUserModel_ID = PROPERTYKEY(GUID('{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}'), 5)


def set_aumid(lnk, aumid):
    comtypes.CoInitialize()
    link = cc.CreateObject(ShellLink)
    pf = link.QueryInterface(IPersistFile)
    pf.Load(lnk, 2)   # STGM_READWRITE
    store = link.QueryInterface(IPropertyStore)
    pv = PROPVARIANT(31, 0, 0, 0, aumid, None)   # VT_LPWSTR
    store.SetValue(ctypes.byref(PKEY_AppUserModel_ID), ctypes.byref(pv))
    store.Commit()
    pf.Save(lnk, True)


def get_aumid(lnk):
    comtypes.CoInitialize()
    link = cc.CreateObject(ShellLink)
    link.QueryInterface(IPersistFile).Load(lnk, 0)
    v = link.QueryInterface(IPropertyStore).GetValue(ctypes.byref(PKEY_AppUserModel_ID))
    return v.pwsz if v.vt == 31 else None


if __name__ == '__main__':
    set_aumid(sys.argv[1], sys.argv[2])
    print(f'{sys.argv[1]}: AppUserModelID = {get_aumid(sys.argv[1])}')
