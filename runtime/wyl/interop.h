#pragma once

// The boundary between generated code and the C++ that a program's extern blocks declare (spec
// interop). The compiler calls each extern function through a noexcept wrapper, which converts its
// arguments and result here, and checks each signature and layout with these traits.

#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <tuple>
#include <type_traits>
#include <utility>

#include "core.h"
#include "optional.h"

namespace wyl {

// A C function pointer's type, which a declaration cannot write directly before its name.
template <class F>
using CPointer = F*;

namespace interop {

template <class W, class C>
constexpr bool Matches();

template <class W, class C>
constexpr bool ResultMatches();

template <class To, class From>
constexpr To Convert(From&& value) noexcept;

template <class R, class F>
constexpr R Result(F&& call) noexcept;

}  // namespace interop

// An `extern fn` value (spec interop.callbacks): the address of a function that captures nothing.
// F is the C function type, so the pointer is the one C uses. The value has the pointer's size and
// is trivially copyable, so Convert turns it into the raw pointer and back. It also converts
// implicitly, because a field of an extern struct has C's own type. The value never crosses a C
// call itself: the wrappers and callbacks pass the raw pointer.
template <class F>
struct CFunction;

template <class R, class... A>
struct CFunction<R(A...)> {
    using Pointer = R (*)(A...);

    Pointer Value = nullptr;

    CFunction() = default;
    constexpr CFunction(Pointer value) noexcept : Value(value) {}

    constexpr operator Pointer() const noexcept { return Value; }

    bool IsNull() const noexcept { return Value == nullptr; }

    // Calls the function, with each argument converted to its C parameter's type and the result to
    // the Wylow type Result, which match.
    template <class Result, class... T>
    Result Call(T&&... arguments) const {
        if constexpr (WYL_DEBUG != 0) {
            if (Value == nullptr) RaisePanic("internal compiler error: null function called", "<runtime>");
        }
        return interop::Result<Result>([&]() -> decltype(auto) { return Value(interop::Convert<A>(std::forward<T>(arguments))...); });
    }
};

// A nullable `extern fn`: the null pointer is null. It converts to and from the raw pointer, as a
// CFunction does.
template <class F>
class Optional<CFunction<F>> {
    CFunction<F> m_Value;

public:
    using Pointer = typename CFunction<F>::Pointer;

    Optional() noexcept = default;
    Optional(std::nullptr_t) noexcept {}
    Optional(const CFunction<F>& value) noexcept : m_Value(value) {}
    Optional(Pointer value) noexcept : m_Value(value) {}

    operator Pointer() const noexcept { return m_Value.Value; }

    bool HasValue() const { return !m_Value.IsNull(); }

    // Flow analysis proved there is a value.
    CFunction<F>& Assumed() {
        if constexpr (WYL_DEBUG != 0) {
            if (m_Value.IsNull()) RaisePanic("internal compiler error: null value used as narrowed", "<runtime>");
        }
        return m_Value;
    }
    const CFunction<F>& Assumed() const { return const_cast<Optional*>(this)->Assumed(); }
};

}  // namespace wyl

namespace wyl::interop {

// The address of a function that has exactly one declaration. For an overload set, deduction of F
// fails, so a requires-expression that names this call is false instead of ill-formed. D makes the
// call dependent, so that the compiler looks at it only where a template is instantiated.
template <class D, class F>
    requires std::is_function_v<F>
constexpr F* Address(F& function) noexcept {
    return &function;
}

// The member pointer of a member function that has exactly one declaration, as Address is for a
// free function.
template <class D, class M, class C>
constexpr M C::* MemberAddress(M C::* member) noexcept {
    return member;
}

// An argument of a call whose C++ name has more than one declaration, passed unchanged. D makes the
// call dependent, so the compiler resolves the overload only where the wrapper is instantiated.
template <class D, class T>
constexpr T&& Pass(T&& value) noexcept {
    return std::forward<T>(value);
}

// The result type and parameter types of a function pointer or a member function pointer.
template <class F>
struct Signature;

template <class R, class... P>
struct Signature<R (*)(P...)> {
    using Result = R;
    using Parameters = std::tuple<P...>;
};

template <class R, class... P>
struct Signature<R (*)(P...) noexcept> : Signature<R (*)(P...)> {};

template <class R, class C, class... P>
struct Signature<R (C::*)(P...)> : Signature<R (*)(P...)> {};

template <class R, class C, class... P>
struct Signature<R (C::*)(P...) const> : Signature<R (*)(P...)> {};

template <class R, class C, class... P>
struct Signature<R (C::*)(P...) noexcept> : Signature<R (*)(P...)> {};

template <class R, class C, class... P>
struct Signature<R (C::*)(P...) const noexcept> : Signature<R (*)(P...)> {};

// The type of the parameter at an index of a function pointer or a member function pointer.
template <class F, std::size_t I>
using Parameter = std::tuple_element_t<I, typename Signature<F>::Parameters>;

// An integer type or a C++ enumeration, but not bool: the types whose values are only their bits.
template <class T>
constexpr bool IS_INTEGER_LIKE = (std::is_integral_v<T> && !std::is_same_v<T, bool>) || std::is_enum_v<T>;

template <class T>
constexpr bool IsSigned() {
    if constexpr (std::is_enum_v<T>)
        return std::is_signed_v<std::underlying_type_t<T>>;
    else
        return std::is_signed_v<T>;
}

template <class T>
constexpr bool IS_FUNCTION_POINTER = std::is_pointer_v<T> && std::is_function_v<std::remove_pointer_t<T>>;

template <class W, class C, std::size_t... I>
constexpr bool ParametersMatch(std::index_sequence<I...>) {
    return (Matches<std::tuple_element_t<I, typename Signature<W>::Parameters>, std::tuple_element_t<I, typename Signature<C>::Parameters>>() && ...);
}

// Whether two function pointers have the same number of parameters, and their results and each of
// their parameters match.
template <class W, class C>
constexpr bool FunctionPointerMatches() {
    using WylowParameters = typename Signature<W>::Parameters;
    using CppParameters = typename Signature<C>::Parameters;
    if constexpr (std::tuple_size_v<WylowParameters> != std::tuple_size_v<CppParameters>)
        return false;
    else
        return ResultMatches<typename Signature<W>::Result, typename Signature<C>::Result>()
            && ParametersMatch<W, C>(std::make_index_sequence<std::tuple_size_v<WylowParameters>>{});
}

// Whether a Wylow type W and a C++ type C match (spec interop.signatures): they are the same type,
// both are integers or enumerations of the same size and signedness, both are function pointers
// whose results and parameters match, or both are pointers whose pointed-to types match, differ
// only in const, or are both one byte wide.
template <class W, class C>
constexpr bool Matches() {
    using Wylow = std::remove_cvref_t<W>;
    using Cpp = std::remove_cvref_t<C>;
    if constexpr (std::is_same_v<Wylow, Cpp>) {
        return true;
    } else if constexpr (IS_INTEGER_LIKE<Wylow> && IS_INTEGER_LIKE<Cpp> && !std::is_same_v<Wylow, char32_t>) {
        return sizeof(Wylow) == sizeof(Cpp) && IsSigned<Wylow>() == IsSigned<Cpp>();
    } else if constexpr (IS_FUNCTION_POINTER<Wylow> || IS_FUNCTION_POINTER<Cpp>) {
        if constexpr (IS_FUNCTION_POINTER<Wylow> && IS_FUNCTION_POINTER<Cpp>)
            return FunctionPointerMatches<Wylow, Cpp>();
        else
            return false;
    } else if constexpr (std::is_pointer_v<Wylow> && std::is_pointer_v<Cpp>) {
        using WylowTarget = std::remove_cv_t<std::remove_pointer_t<Wylow>>;
        using CppTarget = std::remove_cv_t<std::remove_pointer_t<Cpp>>;
        if constexpr (std::is_same_v<WylowTarget, CppTarget>)
            return true;
        else if constexpr (IS_INTEGER_LIKE<WylowTarget> && IS_INTEGER_LIKE<CppTarget>)
            return sizeof(WylowTarget) == sizeof(CppTarget) && (sizeof(WylowTarget) == 1 || Matches<WylowTarget, CppTarget>());
        else
            return std::is_pointer_v<WylowTarget> && Matches<WylowTarget, CppTarget>();
    } else {
        return false;
    }
}

// Whether a declared result type matches the C++ function's: void matches only void.
template <class W, class C>
constexpr bool ResultMatches() {
    if constexpr (std::is_void_v<W> || std::is_void_v<C>)
        return std::is_void_v<W> && std::is_void_v<C>;
    else
        return Matches<W, C>();
}

// One part of a signature check of a function pointer F against a declared result R and declared
// parameters P: ARITY, RESULT, or the parameter at that index. A parameter's part holds whenever
// the counts differ, since the ARITY part reports that.
constexpr int ARITY = -2;
constexpr int RESULT = -1;

template <class F, class R, class... P, std::size_t... I>
constexpr bool ParameterMatches(int part, std::index_sequence<I...>) {
    bool matches[] = {true, Matches<P, Parameter<F, I>>()...};
    return matches[part + 1];
}

template <class F, class R, class... P>
constexpr bool SignaturePart(int part) {
    using Parameters = typename Signature<F>::Parameters;
    constexpr bool sameCount = std::tuple_size_v<Parameters> == sizeof...(P);
    if (part == ARITY)
        return sameCount;
    if (part == RESULT)
        return ResultMatches<R, typename Signature<F>::Result>();
    if constexpr (sameCount)
        return ParameterMatches<F, R, P...>(part, std::index_sequence_for<P...>{});
    else
        return true;
}

// A value converted between two types that match. The conversion keeps the value's bits.
template <class To, class From>
constexpr To Convert(From&& value) noexcept {
    using Source = std::remove_cvref_t<From>;
    if constexpr (std::is_same_v<std::remove_cv_t<To>, Source>)
        return std::forward<From>(value);
    else
        return std::bit_cast<To>(static_cast<Source>(value));
}

// The raw pointer of an `extern fn` value, nullable or not.
template <class F>
constexpr typename CFunction<F>::Pointer RawPointer(const CFunction<F>& value) noexcept {
    return value.Value;
}

template <class F>
constexpr typename CFunction<F>::Pointer RawPointer(const Optional<CFunction<F>>& value) noexcept {
    return value;
}

// Calls a function with each argument converted to the type of its parameter.
template <class R, class F, class... A, std::size_t... I>
R CallConverted(F callee, std::index_sequence<I...>, A... arguments) noexcept {
    return Result<R>([&]() -> decltype(auto) { return callee(Convert<Parameter<F, I>>(arguments)...); });
}

// A C function of exactly the type CP that calls the function Callback, whose type matches CP
// without being it. C calls a function only through its own type.
template <class CP, auto Callback>
struct Adapter;

template <class R, class... A, auto Callback>
struct Adapter<R (*)(A...), Callback> {
    static R Call(A... arguments) noexcept {
        return CallConverted<R>(Callback, std::index_sequence_for<A...>{}, arguments...);
    }
};

// The pointer that C receives as a parameter of type CP for an `extern fn` value. If the value's
// type matches CP without being it, and the value is the address of one of Callbacks, the C
// function receives an Adapter of its own type. Any other value keeps its bits: null, or a pointer
// that C made.
template <class CP, auto... Callbacks, class W>
CP ToC(const W& value) noexcept {
    auto pointer = RawPointer(value);
    if constexpr (std::is_same_v<decltype(pointer), CP> || !FunctionPointerMatches<decltype(pointer), CP>()) {
        return Convert<CP>(pointer);
    } else {
        CP result = std::bit_cast<CP>(pointer);
        static_cast<void>(((pointer == Callbacks && (result = &Adapter<CP, Callbacks>::Call, true)) || ...));
        return result;
    }
}

// The result of a call, converted to the declared result type R. The call is a lambda, so that the
// conversion depends on it and is checked only where a wrapper that makes it is instantiated.
template <class R, class F>
constexpr R Result(F&& call) noexcept {
    if constexpr (std::is_void_v<R>)
        static_cast<void>(call());
    else
        return Convert<R>(call());
}

// The value whose bits are a union's first bytes, read as a T without reading an inactive member.
template <class T, class U>
inline T ReadUnion(const U& from) noexcept {
    static_assert(sizeof(T) <= sizeof(U));
    struct Bytes {
        alignas(T) unsigned char Data[sizeof(T)];
    } bytes;
    std::memcpy(bytes.Data, &from, sizeof(T));
    return std::bit_cast<T>(bytes);
}

}  // namespace wyl::interop
