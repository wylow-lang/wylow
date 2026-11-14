#pragma once

#include "stack_slot.h"

namespace wyl {

// A closure's name for diagnostics, as a template argument so that closures don't store it.
template <size_t N>
struct ClosureName {
    char Text[N];
    consteval ClosureName(const char (&name)[N]) {
        for (size_t i = 0; i < N; ++i) Text[i] = name[i];
    }
};

// An escaping function value: the code to call, and the closure it is called with, which holds
// copies of what its lambda captured and is null when the lambda captures nothing, so that such a
// function allocates nothing and copying it only tests for null. A null code pointer is a null
// function, which only storage that Wylow assigns before reading ever holds, and Optional's null.
template <class R, class... A>
class Function<R(A...)> {
public:
    using Code = R (*)(const void*, A...);

private:
    // Declared first, so that an assignment stores the new code before releasing the old closure,
    // whose destructor may read this function.
    Code m_Code = nullptr;
    const void* m_Callable = nullptr;
    Ref<Object> m_Closure;

public:
    Function() = default;
    Function(Code code, Ref<Object> captured) : m_Code(code), m_Callable(captured.Get()), m_Closure(std::move(captured)) {}
    Function(Code code, Ref<Object> captured, const void* callable) : m_Code(code), m_Callable(callable), m_Closure(std::move(captured)) {}

    Function(const Function&) = default;
    Function(Function&& other) noexcept : m_Code(other.m_Code), m_Callable(other.m_Callable), m_Closure(std::move(other.m_Closure)) {
        other.m_Code = nullptr;
        other.m_Callable = nullptr;
    }
    Function& operator=(const Function&) = default;
    Function& operator=(Function&& other) noexcept {
        if (this == &other) return *this;
        m_Code = other.m_Code;
        m_Callable = other.m_Callable;
        m_Closure = std::move(other.m_Closure);
        other.m_Code = nullptr;
        other.m_Callable = nullptr;
        return *this;
    }

    R operator()(A... arguments) const {
        if constexpr (WYL_DEBUG != 0) {
            if (m_Code == nullptr) RaisePanic("internal compiler error: null function called", "<runtime>");
        }
        return m_Code(m_Callable, std::forward<A>(arguments)...);
    }

    Code GetCode() const { return m_Code; }
    const void* GetClosure() const { return m_Callable; }
    bool IsNull() const { return m_Code == nullptr; }

    void Visit(Visitor& visitor) { wyl::Visit(visitor, m_Closure); }
};

// A non-escaping function value where its callable's type can't be a template parameter: a
// parameter of a virtual or recursive function, or of a function type. It refers to the callable
// without owning it, which Wylow's rules make safe: the callable is a lambda or function value the
// caller holds until the call returns. Made from a Function, it refers to the same closure and calls
// the same code, so that the call is no more indirect than the Function's.
template <class Signature>
class FunctionRef;

template <class R, class... A>
class FunctionRef<R(A...)> {
    const void* m_Callable = nullptr;
    R (*m_Call)(const void*, A...) = nullptr;

public:
    // Null, only as Optional's null.
    FunctionRef() = default;

    FunctionRef(const Function<R(A...)>& f) noexcept : m_Callable(f.GetClosure()), m_Call(f.GetCode()) {}

    template <class F>
        requires(!std::is_same_v<std::remove_cvref_t<F>, FunctionRef> && !std::is_same_v<std::remove_cvref_t<F>, Function<R(A...)>>)
    FunctionRef(const F& f) noexcept
        : m_Callable(std::addressof(f)),
          m_Call([](const void* c, A... arguments) -> R { return (*static_cast<const F*>(c))(std::forward<A>(arguments)...); }) {}

    R operator()(A... arguments) const {
        if constexpr (WYL_DEBUG != 0) {
            if (m_Call == nullptr) RaisePanic("internal compiler error: null function called", "<runtime>");
        }
        return m_Call(m_Callable, std::forward<A>(arguments)...);
    }

    bool IsNull() const { return m_Call == nullptr; }
};

// The closure of one lambda that captures something: copies of the captured values, which the
// collector visits here. Its type doesn't depend on the lambda's code, so that a stack slot for it
// can be declared before the lambda.
template <ClosureName Name, class... C>
class Closure final : public Object {
public:
    std::tuple<C...> Captures;

    template <class... V>
    explicit Closure(V&&... values) : Captures(std::forward<V>(values)...) {}

    void Visit([[maybe_unused]] Visitor& visitor) override {
        std::apply([&](C&... values) { (wyl::Visit(visitor, values), ...); }, Captures);
    }

    const char* ClassName() const override { return Name.Text; }
};

template <ClosureName Name, class... C>
class SharedClosure final : public SharedObject {
public:
    std::tuple<C...> Captures;

    template <class... V>
    explicit SharedClosure(V&&... values) : Captures(std::forward<V>(values)...) {}

    const char* ClassName() const override { return Name.Text; }
};

// The code a function value calls with its closure: Body is a C++ lambda that captures nothing and
// takes the captured values as its first parameters. Capturing nothing, it can be
// default-constructed here, so only the captures are stored. Created is void for a lambda that
// captures nothing, which has no closure.
template <class Signature, class Body, class Created>
struct ClosureCode;

template <class R, class... A, class Body, class Created>
struct ClosureCode<R(A...), Body, Created> {
    static R Call([[maybe_unused]] const void* self, A... arguments) {
        if constexpr (std::is_void_v<Created>) {
            return Body{}(std::forward<A>(arguments)...);
        } else {
            const auto* closure = static_cast<const Created*>(static_cast<const Object*>(self));
            return std::apply([&](const auto&... values) -> R { return Body{}(values..., std::forward<A>(arguments)...); }, closure->Captures);
        }
    }
};

template <class Signature, class Body, class Created>
struct SharedClosureCode;

template <class R, class... A, class Body, class Created>
struct SharedClosureCode<R(A...), Body, Created> {
    static R Call([[maybe_unused]] const void* self, A... arguments) {
        const auto* closure = static_cast<const Created*>(self);
        return std::apply([&](const auto&... values) -> R { return Body{}(values..., std::forward<A>(arguments)...); }, closure->Captures);
    }
};

// `cyclic` is whether the compiler found that the lambda's closures may be part of a cycle.
template <class Signature, ClosureName Name, class Body, class... C>
inline Function<Signature> MakeClosure([[maybe_unused]] bool cyclic, Body, C&&... captures) {
    if constexpr (sizeof...(C) == 0) {
        return Function<Signature>(&ClosureCode<Signature, Body, void>::Call, nullptr);
    } else {
        using Created = Closure<Name, std::remove_cvref_t<C>...>;
        auto* created = new Created(std::forward<C>(captures)...);
        created->SetCyclic(cyclic);
        return Function<Signature>(&ClosureCode<Signature, Body, Created>::Call, Ref<Object>::Adopt(created));
    }
}

template <class Signature, ClosureName Name, class Body, class... C>
inline Function<Signature> MakeSharedClosure(Body, C&&... captures) {
    if constexpr (sizeof...(C) == 0) {
        return Function<Signature>(&ClosureCode<Signature, Body, void>::Call, nullptr);
    } else {
        using Created = SharedClosure<Name, std::remove_cvref_t<C>...>;
        auto* created = new Created(std::forward<C>(captures)...);
        return Function<Signature>(&SharedClosureCode<Signature, Body, Created>::Call, Ref<Object>::Adopt(created), created);
    }
}

// A closure the compiler has proved can't outlive the slot's scope, made in the slot as a class
// object is: the collector never sees it, and the slot's end runs its captures' releases.
template <class Signature, class Body, class Created, class... C>
inline Function<Signature> MakeClosureIn(StackSlot<Created>& slot, Body, C&&... captures) {
    return Function<Signature>(&ClosureCode<Signature, Body, Created>::Call, slot.Make(std::forward<C>(captures)...));
}

// A nullable non-escaping function argument, made where the call is: what it refers to (a lambda, or
// a copy of an escaping function) lives until the end of the full expression, which the call is in.
template <class S>
inline Optional<FunctionRef<S>> OptionalRef(const Optional<FunctionRef<S>>& f) {
    return f;
}

template <class S>
inline Optional<FunctionRef<S>> OptionalRef(const Optional<Function<S>>& f) {
    if (!f.HasValue()) return nullptr;
    return FunctionRef<S>(f.Assumed());
}

template <class S, class F>
inline Optional<FunctionRef<S>> OptionalRef(const F& f) {
    return FunctionRef<S>(f);
}

// A strong copy that keeps a closure alive while it runs, as Pin does for an object.
template <class S>
inline Function<S> Pin(const Function<S>& value) {
    return value;
}

// A function chosen by ?: from several. It holds every callable it could be and calls the chosen
// one directly, so each can still be inlined where the choice is passed to a template.
template <class... F>
class Choice {
    std::tuple<F...> m_Callables;
    int m_Chosen;

    template <int I, class... A>
    auto Call(A&&... arguments) const {
        if constexpr (I + 1 == static_cast<int>(sizeof...(F))) {
            return std::get<I>(m_Callables)(std::forward<A>(arguments)...);
        } else {
            if (m_Chosen == I) return std::get<I>(m_Callables)(std::forward<A>(arguments)...);
            return Call<I + 1>(std::forward<A>(arguments)...);
        }
    }

public:
    Choice(int index, F... options) : m_Callables(std::move(options)...), m_Chosen(index) {}

    template <class... A>
    auto operator()(A&&... arguments) const {
        return Call<0>(std::forward<A>(arguments)...);
    }
};

// A choice's option that is a non-escaping local or parameter: it can't be reassigned and outlives
// the choice, so the choice refers to it rather than copying it.
template <class F>
class Borrowed {
    const F* m_Callable;

public:
    explicit Borrowed(const F& f) : m_Callable(std::addressof(f)) {}

    template <class... A>
    decltype(auto) operator()(A&&... arguments) const {
        return (*m_Callable)(std::forward<A>(arguments)...);
    }
};

template <class F>
Borrowed<F> MakeBorrowed(const F& f) {
    return Borrowed<F>(f);
}

// A choice's option that is a nullable non-escaping function: an option that isn't chosen may still
// be null, so the choice refers to the nullable and unwraps it where it is called, after the check
// that made its option non-null. A narrowed local that flow analysis proved non-null is borrowed
// itself instead.
template <class F>
class Narrowed {
    const F* m_Nullable;

public:
    explicit Narrowed(const F& f) : m_Nullable(std::addressof(f)) {}

    template <class... A>
    decltype(auto) operator()(A&&... arguments) const {
        return m_Nullable->Assumed()(std::forward<A>(arguments)...);
    }
};

template <class F>
Narrowed<F> MakeNarrowed(const F& f) {
    return Narrowed<F>(f);
}

template <class... F>
Choice<F...> Choose(int index, F... options) {
    return Choice<F...>(index, std::move(options)...);
}

// A choice that may also pick an escaping function, which `index` evaluates into `held` only when it
// chooses it: unlike making a lambda, evaluating a field or a call can be observed.
template <class Signature, class I, class... F>
Choice<F..., Function<Signature>> ChooseHeld(I index, F... options) {
    Function<Signature> held;
    int chosen = index(held);
    return Choice<F..., Function<Signature>>(chosen, std::move(options)..., std::move(held));
}

}  // namespace wyl
