-- TERN comparison suite: Lua 5.4
local function clock() return os.clock() * 1000 end

local function bench(name, fn)
  local s = clock()
  local result = fn()
  print(string.format("%s %s %.2f", name, tostring(result), clock() - s))
end

local fib
fib = function(n)
  if n < 2 then return n end
  return fib(n - 1) + fib(n - 2)
end
bench("fib", function() return fib(30) end)

bench("loop", function()
  local total = 0
  for i = 0, 4999999 do total = total + (i % 7) end
  return total
end)

bench("maps", function()
  local m = {}
  for i = 0, 199999 do m["key" .. i] = i end
  local sum = 0
  for i = 0, 199999 do sum = sum + m["key" .. i] end
  return sum
end)

local function make(depth)
  if depth == 0 then return nil end
  return { left = make(depth - 1), right = make(depth - 1) }
end
local function count(node)
  if node == nil then return 0 end
  return 1 + count(node.left) + count(node.right)
end
bench("bintree", function() return count(make(16)) end)

bench("strings", function()
  local parts = {}
  for i = 0, 19999 do parts[#parts + 1] = string.format("item-%d", i) end
  local text = table.concat(parts, ",")
  local split = {}
  for piece in string.gmatch(text, "([^,]+)") do split[#split + 1] = piece end
  return #split + #string.upper(text) + #text
end)
