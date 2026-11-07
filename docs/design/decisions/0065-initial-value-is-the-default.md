# 0065. A field's initial value is its type's default value

- Status: accepted (2026-11-07)
- Spec: `structs.constructors`, `classes.construction`

## Decision

- A field with no initializer whose type has an initial value holds that value after a constructor's
  start. The initial value of a type with a default value is that default value, even when the default
  value is more than empty storage. For `struct Point { int x = 5; }`, a field `Point p` holds a `Point`
  whose `x` is 5.
- A constructor starts from empty storage, as decision 0064 says. It then stores each field's initializer,
  and it stores the type's default value for a field whose default value is more than empty storage
  (`HasDefaultCode`).
- A field that a constructor has to assign gets no such store, so the program never destroys a default
  value that it did not name. This part of decision 0064 stands.
- This decision corrects two statements in decision 0064. Decision 0064 calls the initial value "the value
  empty storage makes", and it says that lowering "runs only the initializers". Both statements are wrong
  for a type whose default value has an initializer.
- The verifier reports a constructor that leaves such a field zeroed, as it already did for a built struct.

## Alternatives

- **Keep the zeroes and make the spec say so.** `new C().p.x` would then be 0 while `Point().x` and a
  memberwise `M().p.x` are 5. One type would have two default values, depending on how the code reached
  the field.
- **Make such a field required, as a field with a destructor is.** Every constructor of a class with a
  `Point` field would then have to assign it. This refuses programs that have a good value to use, and it
  breaks programs that compiled before decision 0064.

## Why

A type's initializers state what a value of that type holds when nothing else says. Decision 0064 removed
the store of the default value to stop a destructor running on a value the program never named, but only
a field the constructor must assign needed that change. For every other field, the removal silently
replaced the default value with zeroes. Both executors made the same mistake, so comparing them could not
find it, and the tests covered only a field's own initializer.
