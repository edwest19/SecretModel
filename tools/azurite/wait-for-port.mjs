// Written by Claude Opus 5.5 (Anthropic) for SecretModel, 2026-11-25. AI-generated.
//
// Waits until a TCP port accepts connections, so CI starts the tests only once Azurite is listening.
// Usage: node wait-for-port.mjs <host> <port> <seconds>. Exits 0 when the port opens, 1 on timeout.
import net from "node:net";

const [host, portText, secondsText] = process.argv.slice(2);
const port = Number(portText);
const deadline = Date.now() + Number(secondsText) * 1000;

function tryOnce() {
  const socket = net.connect({ host, port });
  socket.once("connect", () => {
    socket.end();
    console.log(`${host}:${port} is accepting connections.`);
    process.exit(0);
  });
  socket.once("error", () => {
    socket.destroy();
    if (Date.now() > deadline) {
      console.error(`${host}:${port} did not open within ${secondsText} seconds.`);
      process.exit(1);
    }
    setTimeout(tryOnce, 250);
  });
}

tryOnce();
