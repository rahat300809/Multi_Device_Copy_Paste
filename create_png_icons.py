import zlib
import struct
import os

def make_png(width, height, pixel_func, output_path):
    # PNG signature
    png = bytearray(b'\x89PNG\r\n\x1a\n')
    
    # IHDR chunk
    # width(4), height(4), bit_depth(1=8), color_type(1=6: RGBA), compression(1=0), filter(1=0), interlace(1=0)
    ihdr_data = struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)
    ihdr_crc = zlib.crc32(ihdr_data, zlib.crc32(b'IHDR'))
    png.extend(struct.pack('>I', len(ihdr_data)) + b'IHDR' + ihdr_data + struct.pack('>I', ihdr_crc))
    
    # Raw scanlines
    raw_data = bytearray()
    for y in range(height):
        raw_data.append(0) # filter type None
        for x in range(width):
            r, g, b, a = pixel_func(x, y, width, height)
            raw_data.extend([r, g, b, a])
            
    # IDAT chunk
    compressed = zlib.compress(bytes(raw_data), 9)
    idat_crc = zlib.crc32(compressed, zlib.crc32(b'IDAT'))
    png.extend(struct.pack('>I', len(compressed)) + b'IDAT' + compressed + struct.pack('>I', idat_crc))
    
    # IEND chunk
    iend_crc = zlib.crc32(b'IEND')
    png.extend(struct.pack('>I', 0) + b'IEND' + struct.pack('>I', iend_crc))
    
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, 'wb') as f:
        f.write(png)
    print(f"Created {output_path} ({len(png)} bytes)")

# 1. Launcher icon: Vibrant modern blue background with white clipboard & rounded border
def launcher_pixel(x, y, w, h):
    # Normalized coords from -1 to 1
    nx = (x - w / 2.0) / (w / 2.0)
    ny = (y - h / 2.0) / (h / 2.0)
    dist_sq = nx * nx + ny * ny
    
    # Rounded squircle boundary (radius ~ 0.88)
    corner = (nx**4 + ny**4)
    if corner > 0.75:
        return 0, 0, 0, 0 # Transparent background outside squircle
        
    # Blue gradient (#2563EB to #1D4ED8)
    t = y / float(h)
    bg_r = int(37 + (29 - 37) * t)
    bg_g = int(99 + (78 - 99) * t)
    bg_b = int(235 + (216 - 235) * t)
    
    # Inner clipboard shape
    # Clip board body: x in [0.28, 0.72], y in [0.28, 0.78]
    fx = x / float(w)
    fy = y / float(h)
    
    # Top clip clip
    if 0.42 <= fx <= 0.58 and 0.20 <= fy <= 0.32:
        return 248, 250, 252, 255
    if 0.28 <= fx <= 0.72 and 0.28 <= fy <= 0.78:
        # 3 horizontal lines inside clipboard
        if (0.35 <= fx <= 0.65 and 0.42 <= fy <= 0.46) or \
           (0.35 <= fx <= 0.65 and 0.52 <= fy <= 0.56) or \
           (0.35 <= fx <= 0.55 and 0.62 <= fy <= 0.66):
            return 37, 99, 235, 255
        return 255, 255, 255, 255
        
    return bg_r, bg_g, bg_b, 255

# 2. Notification icon: Pure white silhouette with alpha transparency
def notif_pixel(x, y, w, h):
    fx = x / float(w)
    fy = y / float(h)
    
    # Clip at top
    if 0.40 <= fx <= 0.60 and 0.15 <= fy <= 0.28:
        return 255, 255, 255, 255
    # Board
    if 0.25 <= fx <= 0.75 and 0.25 <= fy <= 0.85:
        # border outline only (2px border for clean status bar rendering)
        border_x = (fx < 0.32 or fx > 0.68)
        border_y = (fy < 0.32 or fy > 0.78)
        # horizontal lines
        line1 = (0.36 <= fx <= 0.64 and 0.42 <= fy <= 0.48)
        line2 = (0.36 <= fx <= 0.64 and 0.54 <= fy <= 0.60)
        line3 = (0.36 <= fx <= 0.55 and 0.66 <= fy <= 0.72)
        if border_x or border_y or line1 or line2 or line3:
            return 255, 255, 255, 255
        return 0, 0, 0, 0
    return 0, 0, 0, 0

res_dir = r"c:\Users\Rahat\Desktop\Copy_Paste\android_client\app\src\main\res"

# Generate launcher icons
make_png(48, 48, launcher_pixel, os.path.join(res_dir, "mipmap-mdpi", "ic_launcher.png"))
make_png(72, 72, launcher_pixel, os.path.join(res_dir, "mipmap-hdpi", "ic_launcher.png"))
make_png(96, 96, launcher_pixel, os.path.join(res_dir, "mipmap-xhdpi", "ic_launcher.png"))
make_png(144, 144, launcher_pixel, os.path.join(res_dir, "mipmap-xxhdpi", "ic_launcher.png"))
make_png(192, 192, launcher_pixel, os.path.join(res_dir, "mipmap-xxxhdpi", "ic_launcher.png"))
make_png(96, 96, launcher_pixel, os.path.join(res_dir, "mipmap", "ic_launcher.png"))
make_png(96, 96, launcher_pixel, os.path.join(res_dir, "drawable", "ic_launcher.png"))

# Generate notification icon in res/drawable
make_png(48, 48, notif_pixel, os.path.join(res_dir, "drawable", "ic_stat_sync.png"))
