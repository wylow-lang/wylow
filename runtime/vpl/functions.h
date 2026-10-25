#pragma once

#include "stack_slot.h"

namespace vpl {

// A closure's name for diagnostics, as a template argument so that closures don't store it.
template <size_t N>
struct ClosureName {
    char text[N];
    consteval ClosureName(const char (&name)[N]) {
        for (size_t i = 0; i < N; ++i) text[i] = name[i];
    }
};

// An escaping function value: the code to call, and the closure it is called with, which holds
// copies of what its lambda captured and is null when the lambda captures nothing, so that such a
// function allocates nothing and copying it only tests for null. A null code pointer is a null
// function, which only storage that VPL assigns before reading ever holds, and Optional's null.
template <class R, class... A>
class Function<R(A...)> {
public:
    using Code = R (*)(const void*, A...);

private:
    // Declared first, so that an assignment stores the new code before releasing the old closure,
    // whose destructor may read this function.
    Code code = nullptr;
    Ref<Object> closure;

public:
    Function() = default;
    Function(Code function, Ref<Object> captured) : code(function), closure(std::move(captured)) {}

    Function(const Function&) = default;
    Function(Function&& other) noexcept : code(other.code), closure(std::move(other.closure)) { other.code = nullptr; }
    Function& operator=(const Function&) = default;
    Function& operator=(Function&& other) noexcept {
        code = other.code;
        closure = std::move(other.closure);
        return *this;
    }

    R operator()(A... arguments) const {
        if constexpr (VPL_DEBUG != 0) {
            if (code == nullptr) panic("internal compiler error: null function called", "<runtime>");
        }
        return code(closure.get(), std::forward<A>(arguments)...);
    }

    Code vpl_code() const { return code; }
    const void* vpl_closure() const { return closure.get(); }
    bool vpl_is_null() const { return code == nullptr; }

    void vpl_visit(Visitor& visitor) { visit(visitor, closure); }
};

// A non-escaping function value where its callable's type can't be a template parameter: a
// parameter of a virtual or recursive function, or of a function type. It refers to the callable
// without owning it, which VPL's rules make safe: the callable is a lambda or function value the
// caller holds until the call returns. Made from a Function, it refers to the same closure and calls
// the same code, so that the call is no more indirect than the Function's.
template <class Signature>
class FunctionRef;

template <class R, class... A>
class FunctionRef<R(A...)> {
    const void* callable = nullptr;
    R (*call)(const void*, A...) = nullptr;

public:
    // Null, only as Optional's null.
    FunctionRef() = default;

    FunctionRef(const Function<R(A...)>& f) noexcept : callable(f.vpl_closure()), call(f.vpl_code()) {}

    template <class F>
        requires(!std::is_same_v<std::remove_cvref_t<F>, FunctionRef> && !std::is_same_v<std::remove_cvref_t<F>, Function<R(A...)>>)
    FunctionRef(const F& f) noexcept
        : callable(std::addressof(f)),
          call([](const void* c, A... arguments) -> R { return (*static_cast<const F*>(c))(std::forward<A>(arguments)...); }) {}

    R operator()(A... arguments) const { return call(callable, std::forward<A>(arguments)...); }

    bool vpl_is_null() const { return call == nullptr; }
};

// The closure of one lambda that captures something: copies of the captured values, which the
// collector visits here. Its type doesn't depend on the lambda's code, so that a stack slot for it
// can be declared before the lambda.
template <ClosureName Name, class... C>
class Closure final : public Object {
public:
    std::tuple<C...> captures;

    template <class... V>
    explicit Closure(V&&... values) : captures(std::forward<V>(values)...) {}

    void vpl_visit([[maybe_unused]] Visitor& visitor) override {
        std::apply([&](C&... values) { (visit(visitor, values), ...); }, captures);
    }

    const char* vpl_class_name() const override { return Name.text; }
};

// The code a function value calls with its closure: Body is a C++ lambda that captures nothing and
// takes the captured values as its first parameters. Capturing nothing, it can be
// default-constructed here, so only the captures are stored. Created is void for a lambda that
// captures nothing, which has no closure.
template <class Signature, class Body, class Created>
struct ClosureCode;

template <class R, class... A, class Body, class Created>
struct ClosureCode<R(A...), Body, Created> {
    static R call([[maybe_unused]] const void* self, A... arguments) {
        if constexpr (std::is_void_v<Created>) {
            return Body{}(std::forward<A>(arguments)...);
        } else {
            const auto* closure = static_cast<const Created*>(static_cast<const Object*>(self));
            return std::apply([&](const auto&... values) -> R { return Body{}(values..., std::forward<A>(arguments)...); }, closure->captures);
        }
    }
};

// `cyclic` is whether the compiler found that the lambda's closures may be part of a cycle.
template <class Signature, ClosureName Name, class Body, class... C>
inline Function<Signature> closure([[maybe_unused]] bool cyclic, Body, C&&... captures) {
    if constexpr (sizeof...(C) == 0) {
        return Function<Signature>(&ClosureCode<Signature, Body, void>::call, nullptr);
    } else {
        using Created = Closure<Name, std::remove_cvref_t<C>...>;
        auto* created = new Created(std::forward<C>(captures)...);
        created->vpl_set_cyclic(cyclic);
        return Function<Signature>(&ClosureCode<Signature, Body, Created>::call, Ref<Object>::adopt(created));
    }
}

// A closure the compiler has proved can't outlive the slot's scope, made in the slot as a class
// object is: the collector never sees it, and the slot's end runs its captures' releases.
template <class Signature, class Body, class Created, class... C>
inline Function<Signature> closure_in(StackSlot<Created>& slot, Body, C&&... captures) {
    return Function<Signature>(&ClosureCode<Signature, Body, Created>::call, slot.make(std::forward<C>(captures)...));
}

// A nullable non-escaping function argument, made where the call is: what it refers to (a lambda, or
// a copy of an escaping function) lives until the end of the full expression, which the call is in.
template <class S>
inline Optional<FunctionRef<S>> optional_ref(const Optional<FunctionRef<S>>& f) {
    return f;
}

template <class S>
inline Optional<FunctionRef<S>> optional_ref(const Optional<Function<S>>& f) {
    if (!f.has_value()) return nullptr;
    return FunctionRef<S>(f.assumed());
}

template <class S, class F>
inline Optional<FunctionRef<S>> optional_ref(const F& f) {
    return FunctionRef<S>(f);
}

// A strong copy that keeps a closure alive while it runs, as pin does for an object.
template <class S>
inline Function<S> pin(const Function<S>& value) {
    return value;
}

// Ends a closure's pin before its scope does.
template <class S>
inline bool unhold(Function<S>& holder) {
    holder = Function<S>();
    return false;
}

// A function chosen by ?: from several. It holds every callable it could be and calls the chosen
// one directly, so each can still be inlined where the choice is passed to a template.
template <class... F>
class Choice {
    std::tuple<F...> callables;
    int chosen;

    template <int I, class... A>
    auto call(A&&... arguments) const {
        if constexpr (I + 1 == static_cast<int>(sizeof...(F))) {
            return std::get<I>(callables)(std::forward<A>(arguments)...);
        } else {
            if (chosen == I) return std::get<I>(callables)(std::forward<A>(arguments)...);
            return call<I + 1>(std::forward<A>(arguments)...);
        }
    }

public:
    Choice(int index, F... options) : callables(std::move(options)...), chosen(index) {}

    template <class... A>
    auto operator()(A&&... arguments) const {
        return call<0>(std::forward<A>(arguments)...);
    }
};

// A choice's option that is a non-escaping local or parameter: it can't be reassigned and outlives
// the choice, so the choice refers to it rather than copying it.
template <class F>
class Borrowed {
    const F* callable;

public:
    explicit Borrowed(const F& f) : callable(std::addressof(f)) {}

    template <class... A>
    decltype(auto) operator()(A&&... arguments) const {
        return (*callable)(std::forward<A>(arguments)...);
    }
};

template <class F>
Borrowed<F> borrowed(const F& f) {
    return Borrowed<F>(f);
}

template <class... F>
Choice<F...> choose(int index, F... options) {
    return Choice<F...>(index, std::move(options)...);
}

// A choice that may also pick an escaping function, which `index` evaluates into `held` only when it
// chooses it: unlike making a lambda, evaluating a field or a call can be observed.
template <class Signature, class I, class... F>
Choice<F..., Function<Signature>> choose_held(I index, F... options) {
    Function<Signature> held;
    int chosen = index(held);
    return Choice<F..., Function<Signature>>(chosen, std::move(options)..., std::move(held));
}

}  // namespace vpl
