"""Input for an isolated Xvfb-hosted compositor. Never connects to the user's display."""
import ctypes as C
import ctypes.util
import os
import time


class Input:
    def __init__(self):
        display = os.environ['DAYDRIVE_WM_TEST_OUTER_DISPLAY']
        self.scale = float(os.environ.get('DAYDRIVE_WM_TEST_SCALE', '1'))
        self.x = C.CDLL(ctypes.util.find_library('X11'))
        self.test = C.CDLL(ctypes.util.find_library('Xtst'))
        self.x.XOpenDisplay.argtypes = [C.c_char_p]
        self.x.XOpenDisplay.restype = C.c_void_p
        self.display = self.x.XOpenDisplay(display.encode())
        assert self.display, 'The isolated Xvfb display is unavailable'
        self.x.XDefaultRootWindow.argtypes = [C.c_void_p]
        self.x.XDefaultRootWindow.restype = C.c_ulong
        self.x.XQueryTree.argtypes = [C.c_void_p, C.c_ulong, C.POINTER(C.c_ulong), C.POINTER(C.c_ulong), C.POINTER(C.POINTER(C.c_ulong)), C.POINTER(C.c_uint)]
        self.x.XFetchName.argtypes = [C.c_void_p, C.c_ulong, C.POINTER(C.c_char_p)]
        self.x.XFree.argtypes = [C.c_void_p]
        self.x.XResizeWindow.argtypes = [C.c_void_p, C.c_ulong, C.c_uint, C.c_uint]
        self.x.XMoveWindow.argtypes = [C.c_void_p, C.c_ulong, C.c_int, C.c_int]
        self.x.XSetInputFocus.argtypes = [C.c_void_p, C.c_ulong, C.c_int, C.c_ulong]
        self.x.XFlush.argtypes = [C.c_void_p]
        self.x.XStringToKeysym.argtypes = [C.c_char_p]
        self.x.XStringToKeysym.restype = C.c_ulong
        self.x.XKeysymToKeycode.argtypes = [C.c_void_p, C.c_ulong]
        self.x.XKeysymToKeycode.restype = C.c_ubyte
        self.test.XTestFakeMotionEvent.argtypes = [C.c_void_p, C.c_int, C.c_int, C.c_int, C.c_ulong]
        self.test.XTestFakeButtonEvent.argtypes = [C.c_void_p, C.c_uint, C.c_int, C.c_ulong]
        self.test.XTestFakeKeyEvent.argtypes = [C.c_void_p, C.c_uint, C.c_int, C.c_ulong]

    def resize_compositor(self, width, height):
        root = self.x.XDefaultRootWindow(self.display)
        returned_root, parent, count = C.c_ulong(), C.c_ulong(), C.c_uint()
        children = C.POINTER(C.c_ulong)()
        assert self.x.XQueryTree(self.display, root, C.byref(returned_root), C.byref(parent), C.byref(children), C.byref(count))
        try:
            for index in range(count.value):
                name = C.c_char_p()
                self.x.XFetchName(self.display, children[index], C.byref(name))
                title = name.value
                if name: self.x.XFree(name)
                if title and b'niri' in title.lower():
                    window = children[index]
                    self.x.XResizeWindow(self.display, window, width, height)
                    self.x.XMoveWindow(self.display, window, 0, 0)
                    self.x.XSetInputFocus(self.display, window, 1, 0)
                    self.x.XFlush(self.display)
                    time.sleep(0.5)
                    return
            raise AssertionError('No isolated niri window found')
        finally:
            if children: self.x.XFree(children)

    def move(self, x, y):
        self.test.XTestFakeMotionEvent(self.display, -1, round(x * self.scale), round(y * self.scale), 0)
        self.x.XFlush(self.display)
        time.sleep(0.08)

    def click(self, x, y, button=1):
        self.move(x, y)
        self.test.XTestFakeButtonEvent(self.display, button, 1, 0)
        self.test.XTestFakeButtonEvent(self.display, button, 0, 50)
        self.x.XFlush(self.display)
        time.sleep(0.2)

    def key(self, name, down=True):
        symbol = self.x.XStringToKeysym(name.encode())
        code = self.x.XKeysymToKeycode(self.display, symbol)
        assert code, f'No keycode for {name}'
        self.test.XTestFakeKeyEvent(self.display, code, int(down), 0)
        self.x.XFlush(self.display)

    def scroll(self, x, y, steps):
        self.move(x, y)
        for _ in range(abs(steps)):
            button = 5 if steps > 0 else 4
            self.test.XTestFakeButtonEvent(self.display, button, 1, 0)
            self.test.XTestFakeButtonEvent(self.display, button, 0, 10)
            self.x.XFlush(self.display)
            time.sleep(0.08)

    def press(self, name):
        self.key(name)
        self.key(name, False)
        time.sleep(0.06)

    def text(self, value):
        for letter in value:
            self.press('space' if letter == ' ' else letter)


if __name__ == '__main__':
    Input().resize_compositor(1600, 1000)
