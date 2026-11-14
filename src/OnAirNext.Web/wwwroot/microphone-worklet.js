/* 48 kHz stereo float32, 20 ms messages. Device conversion belongs to AudioContext. */
class RadioTeduMicrophone extends AudioWorkletProcessor {
  constructor() { super(); this.block = new Float32Array(1920); this.offset = 0; }
  process(inputs, outputs) {
    for (const output of outputs) for (const channel of output) channel.fill(0);
    const channels = inputs[0]; if (!channels?.length) return true;
    const left = channels[0], right = channels[1] ?? left;
    for (let i = 0; i < left.length; i++) {
      this.block[this.offset++] = Number.isFinite(left[i]) ? Math.max(-8, Math.min(8, left[i])) : 0;
      this.block[this.offset++] = Number.isFinite(right[i]) ? Math.max(-8, Math.min(8, right[i])) : 0;
      if (this.offset === this.block.length) {
        this.port.postMessage(this.block.buffer, [this.block.buffer]);
        this.block = new Float32Array(1920); this.offset = 0;
      }
    }
    return true;
  }
}
registerProcessor('radiotedu-microphone', RadioTeduMicrophone);
