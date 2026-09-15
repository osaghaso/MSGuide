"""Small synchronous adapter over httpx ASGITransport; no Starlette/httpx version coupling."""
import asyncio
import httpx


class TestClient:
    __test__ = False

    def __init__(self, app, base_url="http://localhost", headers=None):
        self.app = app
        self.runner = asyncio.Runner()
        self.client = httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url=base_url, headers=headers)
        self.headers = self.client.headers
        self.portal = self

    def __enter__(self):
        self.lifespan = self.app.router.lifespan_context(self.app)
        self.runner.run(self.lifespan.__aenter__())
        return self

    def __exit__(self, *exc):
        try:
            self.runner.run(self.client.aclose())
            self.runner.run(self.lifespan.__aexit__(*exc))
        finally:
            self.runner.close()

    def call(self, function):
        return self.runner.run(function())

    def post(self, path, **kwargs):
        return self.runner.run(self.client.post(path, **kwargs))

    def get(self, path, **kwargs):
        return self.runner.run(self.client.get(path, **kwargs))