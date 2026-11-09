export function writeFrame(stream: NodeJS.WritableStream, frame: unknown): Promise<void> {
  return new Promise((resolve, reject) => {
    try {
      const line = JSON.stringify(frame) + "\n";
      stream.write(line, "utf8", (err) => (err ? reject(err) : resolve()));
    } catch (err) {
      reject(err);
    }
  });
}

export interface FrameHandlers {
  onFrame: (frame: unknown) => void;
  onEnd: () => void;
}

export function readFrames(stream: NodeJS.ReadableStream, handlers: FrameHandlers): void {
  let buffer = "";
  let ended = false;

  const finish = () => {
    if (ended) {
      return;
    }
    ended = true;
    handlers.onEnd();
  };

  stream.setEncoding("utf8");
  stream.on("data", (chunk: string) => {
    buffer += chunk;
    let newlineIndex = buffer.indexOf("\n");
    while (newlineIndex !== -1) {
      const line = buffer.slice(0, newlineIndex).trim();
      buffer = buffer.slice(newlineIndex + 1);
      if (line.length > 0) {
        try {
          handlers.onFrame(JSON.parse(line));
        } catch {
          handlers.onFrame({ __parseError: true, raw: line });
        }
      }
      newlineIndex = buffer.indexOf("\n");
    }
  });
  stream.on("end", finish);
  stream.on("close", finish);
}
