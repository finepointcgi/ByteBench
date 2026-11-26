import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: { include: ['tests/e2e/**/*.test.ts'], environment: 'node', testTimeout: 180_000, hookTimeout: 240_000, fileParallelism: false },
});
