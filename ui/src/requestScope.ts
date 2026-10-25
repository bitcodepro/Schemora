/** Discard superseded work even when a transport has already received its response and cannot abort it. */
export class RequestScope {
  private revision = 0;
  private controller: AbortController | null = null;

  begin() {
    this.cancel();
    const revision = this.revision;
    const controller = new AbortController();
    this.controller = controller;
    return { signal: controller.signal, current: () => this.revision === revision && !controller.signal.aborted };
  }

  cancel() {
    this.revision++;
    this.controller?.abort();
    this.controller = null;
  }
}
