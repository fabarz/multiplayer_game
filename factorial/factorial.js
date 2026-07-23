function factorial(n) {
    if (n < 0 || !Number.isInteger(n)) {
        return "Error";
    }

    let a = 1;

    for (let x = 1; x <= n; x++) {
        a *= x;
    }

    return n === 0 ? 1 : a;
}

module.exports = factorial;