// TERN comparison suite: Node.js
const { performance } = require('node:perf_hooks');

function bench(name, fn) {
  const start = performance.now();
  const result = fn();
  const elapsed = performance.now() - start;
  console.log(`${name} ${result} ${elapsed.toFixed(2)}`);
}

function fib(n) {
  if (n < 2) return n;
  return fib(n - 1) + fib(n - 2);
}
bench('fib', () => fib(30));

bench('loop', () => {
  let total = 0;
  for (let i = 0; i < 5000000; i++) total = total + (i % 7);
  return total;
});

bench('maps', () => {
  const m = new Map();
  for (let i = 0; i < 200000; i++) m.set('key' + i, i);
  let sum = 0;
  for (let i = 0; i < 200000; i++) sum += m.get('key' + i);
  return sum;
});

function make(depth) {
  if (depth === 0) return null;
  return { left: make(depth - 1), right: make(depth - 1) };
}
function count(node) {
  if (node === null) return 0;
  return 1 + count(node.left) + count(node.right);
}
bench('bintree', () => count(make(16)));

bench('strings', () => {
  const parts = [];
  for (let i = 0; i < 20000; i++) parts.push(`item-${i}`);
  const text = parts.join(',');
  const split = text.split(',');
  return split.length + text.toUpperCase().length + text.length;
});
