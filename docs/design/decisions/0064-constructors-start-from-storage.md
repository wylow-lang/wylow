# 0064. A constructor starts from empty storage, so a field with a destructor must be assigned

- Status: accepted (2026-11-06), corrected by 0065
- Spec: `structs.constructors`, `structs.constructors.memberwise`, `classes.construction`,
  `flow.constructors`

## Decision

- A constructor, of a struct or of a class, starts from empty storage and runs the field
  initializers. A field with no initializer holds its type's **initial value**, which is the value
  empty storage makes: null for a nullable value or a reference, and the type's default value for a
  type that has one.
- A type that runs a destructor has no initial value, because empty storage holds no value of it. A
  struct or fixed array that contains such a type has none either. `TypeDefaults.HasInitialValue`
  answers this question for a type.
- A field with no initializer whose type has no initial value is required. Every constructor assigns
  it before using `this`, and a memberwise constructor requires an argument for it.
  `FieldSymbol.HasDefault` is the one answer, so `RequiresAssignment`, the arguments a memberwise call
  may leave out, and the verifier's required fields change together.
- `HasDefaultCode` now answers what its summary always said, whether the type's default value is
  more than empty storage: a type with no initial value does have default code. A struct's
  `make_default` therefore stores `default(fieldType)` for such a field, and `DefaultValue<T>()`
  makes a real value. `T()` still works for such a struct, and the field it makes holds its default.
- Lowering starts a struct constructor from `IrZeroed` and runs only the initializers. A class
  constructor already starts from an object whose fields are empty storage, and it now runs only the
  initializers as well. The verifier allows a zeroed start for a struct constructor.
- WYL0143 says the field "has no initial value", which is true both of a field whose type has no
  default value and of a field whose type's default is more than empty storage.

## Alternatives

- **Keep starting from the default value.** A constructor then makes every field's default and
  destroys it when the body assigns over it. A type argument's destructor ran on a default value the
  program never named (`Holder<Token>(token)` destroyed a `Token(0)`), which is observable behavior,
  and every generic constructor paid to make and destroy defaults.
- **Start from empty storage without making such a field required.** A live struct or object would
  then hold a field whose storage never became a value. Reading the field reads zeros that are not a
  value, and its destructor never runs. The two changes belong together.
- **Require every type that runs a destructor, without the nullable and reference exceptions.** A
  nullable value's initial value is null and a reference's is null, so requiring those fields would
  refuse programs that have nothing to assign. `HasInitialValue` answers by value containment, not
  by `RunsDestructor`.
- **Narrow `IsDefaultable` as well.** `T()` would then be refused for a struct containing a
  destructor-bearing field, even though `make_default` can make that value. Keeping the two
  questions apart lets `T()` work and changes only what a constructor starts with.

## Why

The two rules are one rule. A constructor's start value is empty storage, so a field may be left
alone exactly when empty storage is a value of its type. Making the other field required keeps a
live value from containing storage that never became a value, and it removes the invisible
construction and destruction of defaults that every constructor used to perform.
