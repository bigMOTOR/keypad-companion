import ctypes, json
cg = ctypes.CDLL('/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics')
ds = ctypes.CDLL('/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices')
cg.CGMainDisplayID.restype = ctypes.c_uint32
cg.CGDisplayIsAsleep.argtypes = [ctypes.c_uint32]
cg.CGDisplayIsAsleep.restype = ctypes.c_bool
ds.DisplayServicesGetBrightness.argtypes = [ctypes.c_uint32, ctypes.POINTER(ctypes.c_float)]
ds.DisplayServicesGetBrightness.restype = ctypes.c_int
display = cg.CGMainDisplayID()
value = ctypes.c_float()
if not display or cg.CGDisplayIsAsleep(display):
    print(json.dumps({'available': False, 'asleep': True}))
else:
    result = ds.DisplayServicesGetBrightness(display, ctypes.byref(value))
    print(json.dumps({'available': result == 0, 'brightness': float(value.value) if result == 0 else None}))
