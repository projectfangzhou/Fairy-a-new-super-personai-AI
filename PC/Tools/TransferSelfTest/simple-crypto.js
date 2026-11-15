// AES-256-GCM and SHA-256, small enough to inline and written to be checked rather than trusted.
//
// The phone page needs both because a browser only exposes its built-in crypto on a secure origin,
// and this test is served over plain HTTP on a LAN address. Everything here is verified against
// Node's crypto in the tool's own test run, so correctness does not rest on my reading of the spec.
//
// Scope on purpose: AES forward cipher only (GCM never needs the inverse), bit-serial GHASH (slow
// but short), and a straightforward SHA-256. Fine for a few megabytes; not for bulk work.
(function (global) {
  'use strict';

  // ---------------------------------------------------------------- AES-256

  const SBOX = (function () {
    const box = new Uint8Array(256);
    const xtime = a => ((a << 1) ^ (a & 0x80 ? 0x1b : 0)) & 0xff;
    const mul = (a, b) => {
      let result = 0;
      while (b) {
        if (b & 1) result ^= a;
        a = xtime(a);
        b >>= 1;
      }
      return result;
    };
    const inverse = a => {
      if (a === 0) return 0;
      for (let i = 1; i < 256; i++) if (mul(a, i) === 1) return i;
      return 0;
    };
    for (let i = 0; i < 256; i++) {
      const x = inverse(i);
      let s = x, t = x;
      for (let k = 0; k < 4; k++) {
        t = ((t << 1) | (t >>> 7)) & 0xff;
        s ^= t;
      }
      box[i] = (s ^ 0x63) & 0xff;
    }
    return box;
  })();

  function expandKey(key) {
    if (key.length !== 32) throw new Error('需要 32 字节密钥');
    const Nk = 8, Nr = 14;
    const words = new Uint8Array(16 * (Nr + 1));
    words.set(key);
    let rcon = 1;
    for (let i = Nk; i < 4 * (Nr + 1); i++) {
      let t0 = words[4 * (i - 1)], t1 = words[4 * (i - 1) + 1], t2 = words[4 * (i - 1) + 2], t3 = words[4 * (i - 1) + 3];
      if (i % Nk === 0) {
        const rotated = t0;
        t0 = SBOX[t1] ^ rcon; t1 = SBOX[t2]; t2 = SBOX[t3]; t3 = SBOX[rotated];
        rcon = ((rcon << 1) ^ (rcon & 0x80 ? 0x1b : 0)) & 0xff;
      } else if (i % Nk === 4) {
        t0 = SBOX[t0]; t1 = SBOX[t1]; t2 = SBOX[t2]; t3 = SBOX[t3];
      }
      const base = 4 * (i - Nk);
      words[4 * i] = words[base] ^ t0;
      words[4 * i + 1] = words[base + 1] ^ t1;
      words[4 * i + 2] = words[base + 2] ^ t2;
      words[4 * i + 3] = words[base + 3] ^ t3;
    }
    return words;
  }

  const xtime = a => ((a << 1) ^ (a & 0x80 ? 0x1b : 0)) & 0xff;

  function encryptBlock(roundKeys, input, output) {
    const state = Uint8Array.from(input);
    for (let i = 0; i < 16; i++) state[i] ^= roundKeys[i];

    for (let round = 1; round <= 14; round++) {
      // SubBytes + ShiftRows
      const shifted = new Uint8Array(16);
      for (let column = 0; column < 4; column++) {
        for (let row = 0; row < 4; row++) {
          shifted[4 * column + row] = SBOX[state[4 * ((column + row) % 4) + row]];
        }
      }
      // MixColumns (skipped in the final round)
      if (round !== 14) {
        for (let column = 0; column < 4; column++) {
          const a0 = shifted[4 * column], a1 = shifted[4 * column + 1];
          const a2 = shifted[4 * column + 2], a3 = shifted[4 * column + 3];
          const all = a0 ^ a1 ^ a2 ^ a3;
          state[4 * column] = a0 ^ all ^ xtime(a0 ^ a1);
          state[4 * column + 1] = a1 ^ all ^ xtime(a1 ^ a2);
          state[4 * column + 2] = a2 ^ all ^ xtime(a2 ^ a3);
          state[4 * column + 3] = a3 ^ all ^ xtime(a3 ^ a0);
        }
      } else {
        state.set(shifted);
      }
      // AddRoundKey
      for (let i = 0; i < 16; i++) state[i] ^= roundKeys[16 * round + i];
    }

    output.set(state);
  }

  // ---------------------------------------------------------------- GHASH

  function ghashMultiply(x, y) {
    const z = new Uint8Array(16);
    const v = Uint8Array.from(y);
    for (let i = 0; i < 128; i++) {
      if ((x[i >> 3] >> (7 - (i & 7))) & 1) {
        for (let j = 0; j < 16; j++) z[j] ^= v[j];
      }
      const lsb = v[15] & 1;
      for (let j = 15; j > 0; j--) v[j] = ((v[j] >>> 1) | ((v[j - 1] & 1) << 7)) & 0xff;
      v[0] = v[0] >>> 1;
      if (lsb) v[0] ^= 0xe1;
    }
    return z;
  }

  function ghash(key, aad, ciphertext) {
    const roundKeys = expandKey(key);
    const h = new Uint8Array(16);
    encryptBlock(roundKeys, new Uint8Array(16), h);

    let y = new Uint8Array(16);
    const absorb = block => {
      for (let i = 0; i < 16; i++) y[i] ^= block[i];
      y = ghashMultiply(y, h);
    };

    const padded = (data) => {
      const blocks = Math.ceil(data.length / 16);
      for (let i = 0; i < blocks; i++) {
        const block = new Uint8Array(16);
        block.set(data.subarray(i * 16, Math.min(data.length, (i + 1) * 16)));
        absorb(block);
      }
    };

    padded(aad);
    padded(ciphertext);

    const lengths = new Uint8Array(16);
    const view = new DataView(lengths.buffer);
    view.setUint32(0, Math.floor(aad.length * 8 / 4294967296), false);
    view.setUint32(4, (aad.length * 8) >>> 0, false);
    view.setUint32(8, Math.floor(ciphertext.length * 8 / 4294967296), false);
    view.setUint32(12, (ciphertext.length * 8) >>> 0, false);
    absorb(lengths);

    return y;
  }

  function ctr(key, nonce, input) {
    const roundKeys = expandKey(key);
    const output = new Uint8Array(input.length);
    const counter = new Uint8Array(16);
    counter.set(nonce);
    // 96-bit nonce: J0 is nonce||0x00000001, so the first data block starts at counter 2.
    counter[15] = 2;

    for (let offset = 0; offset < input.length; offset += 16) {
      const stream = new Uint8Array(16);
      encryptBlock(roundKeys, counter, stream);
      const take = Math.min(16, input.length - offset);
      for (let i = 0; i < take; i++) output[offset + i] = input[offset + i] ^ stream[i];

      for (let i = 15; i >= 12; i--) {
        counter[i] = (counter[i] + 1) & 0xff;
        if (counter[i] !== 0) break;
      }
    }
    return output;
  }

  function tagFor(key, nonce, aad, ciphertext) {
    const roundKeys = expandKey(key);
    const j0 = new Uint8Array(16);
    j0.set(nonce);
    j0[15] = 1;

    const mask = new Uint8Array(16);
    encryptBlock(roundKeys, j0, mask);

    const s = ghash(key, aad, ciphertext);
    const tag = new Uint8Array(16);
    for (let i = 0; i < 16; i++) tag[i] = s[i] ^ mask[i];
    return tag;
  }

  function encryptGcm(key, nonce, plaintext, aad) {
    const ciphertext = ctr(key, nonce, plaintext);
    const tag = tagFor(key, nonce, aad, ciphertext);
    return { ciphertext, tag };
  }

  function decryptGcm(key, nonce, ciphertext, tag, aad) {
    const expected = tagFor(key, nonce, aad, ciphertext);
    let difference = 0;
    for (let i = 0; i < 16; i++) difference |= expected[i] ^ tag[i];
    if (difference !== 0) throw new Error('校验失败（密钥不符或内容被改动）');
    return ctr(key, nonce, ciphertext);
  }

  // ---------------------------------------------------------------- SHA-256

  const K = new Uint32Array([
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
  ]);

  function sha256(bytes) {
    const h = new Uint32Array([
      0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
    ]);

    const length = bytes.length;
    const paddedLength = Math.ceil((length + 9) / 64) * 64;
    const buffer = new Uint8Array(paddedLength);
    buffer.set(bytes);
    buffer[length] = 0x80;

    const view = new DataView(buffer.buffer);
    view.setUint32(paddedLength - 8, Math.floor(length * 8 / 4294967296), false);
    view.setUint32(paddedLength - 4, (length * 8) >>> 0, false);

    const w = new Uint32Array(64);
    const rotr = (x, n) => ((x >>> n) | (x << (32 - n))) >>> 0;

    for (let offset = 0; offset < paddedLength; offset += 64) {
      for (let i = 0; i < 16; i++) w[i] = view.getUint32(offset + i * 4, false);
      for (let i = 16; i < 64; i++) {
        const s0 = (rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3)) >>> 0;
        const s1 = (rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10)) >>> 0;
        w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0;
      }

      let [a, b, c, d, e, f, g, hh] = h;
      for (let i = 0; i < 64; i++) {
        const s1 = (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) >>> 0;
        const choice = ((e & f) ^ (~e & g)) >>> 0;
        const temp1 = (hh + s1 + choice + K[i] + w[i]) >>> 0;
        const s0 = (rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) >>> 0;
        const majority = ((a & b) ^ (a & c) ^ (b & c)) >>> 0;
        const temp2 = (s0 + majority) >>> 0;

        hh = g; g = f; f = e;
        e = (d + temp1) >>> 0;
        d = c; c = b; b = a;
        a = (temp1 + temp2) >>> 0;
      }

      h[0] = (h[0] + a) >>> 0; h[1] = (h[1] + b) >>> 0; h[2] = (h[2] + c) >>> 0; h[3] = (h[3] + d) >>> 0;
      h[4] = (h[4] + e) >>> 0; h[5] = (h[5] + f) >>> 0; h[6] = (h[6] + g) >>> 0; h[7] = (h[7] + hh) >>> 0;
    }

    const out = new Uint8Array(32);
    const outView = new DataView(out.buffer);
    for (let i = 0; i < 8; i++) outView.setUint32(i * 4, h[i], false);
    return out;
  }

  global.SimpleCrypto = { encryptGcm, decryptGcm, sha256 };
})(typeof window !== 'undefined' ? window : globalThis);
