-- VanillaItemDumper / json.lua
--
-- Minimal pretty-print JSON encoder tailored to Windrose item dumps.
-- Design goals:
--   * Tab-indented output that matches the formatting used by the existing
--     "Stack_Size_Changes_x04_P" reference mod (tabs, one key per line).
--   * Preserves declaration order of keys via ordered-object containers
--     (we cannot rely on Lua hash-table iteration order).
--   * Distinguishes empty arrays from empty objects (UE serialisers emit `[]`
--     for things like `"AdditionalIcons": []` even when empty).
--   * Keeps full double precision for floats so we round-trip values like
--     0.10000000149011612 without truncation.
--
-- Public API:
--   json.obj()                -> new ordered object container
--   json.set(o, key, value)   -> append/replace key on an ordered object
--   json.arr()                -> new explicit-array container
--   json.push(a, value)       -> append to an explicit-array
--   json.encode(value)        -> serialised JSON string (pretty-printed)
--
-- Containers are plain Lua tables with a hidden `__kind` marker so the encoder
-- can tell objects from arrays unambiguously even when both are empty.

local json = {}

local KIND_OBJ = "obj"
local KIND_ARR = "arr"

function json.obj()
    return { __kind = KIND_OBJ, _order = {}, _values = {} }
end

function json.set(o, key, value)
    if o._values[key] == nil then
        table.insert(o._order, key)
    end
    o._values[key] = value
    return o
end

function json.arr()
    return { __kind = KIND_ARR, _items = {} }
end

function json.push(a, value)
    table.insert(a._items, value)
    return a
end

-- ---------------------------------------------------------------------------
-- Encoder
-- ---------------------------------------------------------------------------

local encode_value

local function escape_str(s)
    -- Lua patterns can't easily match the full JSON escape set, so we go char
    -- by char for any byte that needs special treatment.
    local out = {}
    for i = 1, #s do
        local b = s:byte(i)
        if b == 0x22 then        -- "
            out[#out + 1] = "\\\""
        elseif b == 0x5C then    -- backslash
            out[#out + 1] = "\\\\"
        elseif b == 0x08 then
            out[#out + 1] = "\\b"
        elseif b == 0x09 then
            out[#out + 1] = "\\t"
        elseif b == 0x0A then
            out[#out + 1] = "\\n"
        elseif b == 0x0C then
            out[#out + 1] = "\\f"
        elseif b == 0x0D then
            out[#out + 1] = "\\r"
        elseif b < 0x20 then
            out[#out + 1] = string.format("\\u%04x", b)
        else
            out[#out + 1] = string.sub(s, i, i)
        end
    end
    return '"' .. table.concat(out) .. '"'
end

local function encode_number(n)
    if n ~= n then return "null" end                  -- NaN -> null
    if n == math.huge or n == -math.huge then         -- Inf -> null
        return "null"
    end
    -- Treat exact integers as ints (no decimal point), everything else gets
    -- maximum precision so floats survive the round-trip.
    if math.type then
        if math.type(n) == "integer" then
            return string.format("%d", n)
        end
    end
    if n == math.floor(n) and math.abs(n) < 1e16 then
        return string.format("%d", n)
    end
    return string.format("%.17g", n)
end

local function indent(level)
    return string.rep("\t", level)
end

encode_value = function(v, level)
    local t = type(v)
    if v == nil or t == "nil" then
        return "null"
    elseif t == "boolean" then
        return v and "true" or "false"
    elseif t == "number" then
        return encode_number(v)
    elseif t == "string" then
        return escape_str(v)
    elseif t == "table" then
        local kind = v.__kind
        if kind == KIND_OBJ then
            if #v._order == 0 then return "{}" end
            local parts = {}
            local pad = indent(level + 1)
            for _, key in ipairs(v._order) do
                parts[#parts + 1] = pad
                    .. escape_str(key) .. ": "
                    .. encode_value(v._values[key], level + 1)
            end
            return "{\n" .. table.concat(parts, ",\n") .. "\n" .. indent(level) .. "}"
        elseif kind == KIND_ARR then
            if #v._items == 0 then return "[]" end
            local parts = {}
            local pad = indent(level + 1)
            for _, item in ipairs(v._items) do
                parts[#parts + 1] = pad .. encode_value(item, level + 1)
            end
            return "[\n" .. table.concat(parts, ",\n") .. "\n" .. indent(level) .. "]"
        else
            -- Plain Lua table fallback: detect array vs. object by keys.
            -- (We prefer the explicit containers above, but be lenient.)
            local n = #v
            local hasNonArr = false
            for k in pairs(v) do
                if type(k) ~= "number" then hasNonArr = true break end
            end
            if not hasNonArr and n > 0 then
                local parts = {}
                local pad = indent(level + 1)
                for i = 1, n do
                    parts[#parts + 1] = pad .. encode_value(v[i], level + 1)
                end
                return "[\n" .. table.concat(parts, ",\n") .. "\n" .. indent(level) .. "]"
            elseif not hasNonArr and n == 0 then
                return "[]"
            else
                local parts = {}
                local pad = indent(level + 1)
                local keys = {}
                for k in pairs(v) do keys[#keys + 1] = tostring(k) end
                table.sort(keys)
                for _, k in ipairs(keys) do
                    parts[#parts + 1] = pad
                        .. escape_str(k) .. ": "
                        .. encode_value(v[k], level + 1)
                end
                if #parts == 0 then return "{}" end
                return "{\n" .. table.concat(parts, ",\n") .. "\n" .. indent(level) .. "}"
            end
        end
    end
    -- Userdata or function fallthrough — best-effort string repr.
    return escape_str(tostring(v))
end

function json.encode(v)
    return encode_value(v, 0)
end

return json
