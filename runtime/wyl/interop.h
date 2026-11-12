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

// Whether a Wylow type W and a C++ type C match (spec interop.signatures): they are the same type,
// both are integers or enumerations of the same size and signedness, or both are pointers whose
// pointed-to types match, differ only in const, or are both one byte wide.
template <class W, class C>
constexpr bool Matches() {
    using Wylow = std::remove_cvref_t<W>;
    using Cpp = std::remove_cvref_t<C>;
    if constexpr (std::is_same_v<Wylow, Cpp>) {
        return true;
    } else if constexpr (IS_INTEGER_LIKE<Wylow> && IS_INTEGER_LIKE<Cpp> && !std::is_same_v<Wylow, char32_t>) {
        return sizeof(Wylow) == sizeof(Cpp) && IsSigned<Wylow>() == IsSigned<Cpp>();
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
