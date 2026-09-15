"""Bounded, in-memory PNG validation. No model or contract imports."""

import base64
import binascii
from io import BytesIO
import warnings

MAX_IMAGE_BYTES = 2_000_000
MAX_BASE64_CHARS = 2_666_668
MAX_DIMENSION = 1600
MAX_PIXELS = 2_560_000


def sanitize_png(value: str, width: int, height: int) -> str:
    """Return canonical base64 of pixel-only PNG; never include input in errors."""
    try:
        if not isinstance(value, str) or not 0 < len(value) <= MAX_BASE64_CHARS:
            raise ValueError
        raw = base64.b64decode(value, validate=True)
        if len(raw) > MAX_IMAGE_BYTES or base64.b64encode(raw).decode("ascii") != value:
            raise ValueError
        # Lazy import keeps the UIA-only demo usable without the optional image dependency.
        from PIL import Image

        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(BytesIO(raw), formats=["PNG"]) as image:
                w, h = image.size
                if (not 0 < w <= MAX_DIMENSION or not 0 < h <= MAX_DIMENSION
                        or w * h > MAX_PIXELS or (w, h) != (width, height)
                        or image.n_frames != 1):
                    raise ValueError
                image.verify()
            with Image.open(BytesIO(raw), formats=["PNG"]) as image:
                image.load()
                # A new pixel buffer drops text, EXIF, ICC, DPI and all other metadata.
                pixels = image.convert("RGBA")
                with Image.frombytes("RGBA", pixels.size, pixels.tobytes()) as clean:
                    output = BytesIO()
                    clean.save(output, format="PNG")
                    sanitized = output.getvalue()
            if len(sanitized) > MAX_IMAGE_BYTES:
                raise ValueError
            return base64.b64encode(sanitized).decode("ascii")
    except (ValueError, TypeError, binascii.Error, OSError, SyntaxError, Warning):
        raise ValueError("Invalid PNG image") from None
    except Exception:
        # Includes Pillow bomb errors and unavailable decoder; fail closed without payloads.
        raise ValueError("Invalid PNG image") from None