"""Image files the server keeps in <data_dir>: operation pictures (Mastercam screenshots like
Pic00084197.png.cam, uploaded by the helper) and user profile pictures. The server never reads
the network share."""
import os
from datetime import UTC, datetime
from pathlib import Path

_SIGNATURES = {
    b"\x89PNG\r\n\x1a\n": "image/png",
    b"\xff\xd8\xff": "image/jpeg",
}


def media_type(data: bytes) -> str | None:
    for sig, mt in _SIGNATURES.items():
        if data.startswith(sig):
            return mt
    return None


class ImageStore:
    """One image per id in <data_dir>/<subdir>/<id>."""

    def __init__(self, subdir: str, max_bytes: int):
        self.subdir = subdir
        self.max_bytes = max_bytes
        self._dir: Path | None = None

    def configure(self, data_dir: str) -> None:
        self._dir = Path(data_dir) / self.subdir
        self._dir.mkdir(parents=True, exist_ok=True)

    def path_for(self, item_id: int) -> Path:
        if self._dir is None:
            raise RuntimeError("pictures.configure() was not called")
        return self._dir / str(item_id)

    def save(self, item_id: int, data: bytes) -> None:
        target = self.path_for(item_id)
        tmp = target.with_suffix(".tmp")
        tmp.write_bytes(data)
        os.replace(tmp, target)  # atomic: readers never see half a picture

    def read(self, item_id: int) -> bytes | None:
        try:
            return self.path_for(item_id).read_bytes()
        except FileNotFoundError:
            return None

    def delete(self, item_id: int) -> bool:
        try:
            self.path_for(item_id).unlink()
            return True
        except FileNotFoundError:
            return False

    def updated_at(self, item_id: int) -> str | None:
        """Time of the stored image (UTC, ISO), or None. Used for cache busting and by the
        helper to decide whether its local picture is newer."""
        try:
            ts = self.path_for(item_id).stat().st_mtime
        except (FileNotFoundError, RuntimeError):
            return None
        return datetime.fromtimestamp(ts, UTC).replace(tzinfo=None).isoformat(timespec="seconds")


operations = ImageStore("pictures", 15 * 1024 * 1024)
avatars = ImageStore("avatars", 2 * 1024 * 1024)


def configure(data_dir: str) -> None:
    """Called once per app (see main.create_app)."""
    operations.configure(data_dir)
    avatars.configure(data_dir)


# Operation pictures (kept as module functions; used across the routers)
MAX_BYTES = operations.max_bytes
path_for = operations.path_for
save = operations.save
delete = operations.delete
updated_at = operations.updated_at
