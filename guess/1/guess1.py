import random  # 1. Bring in a tool to let the computer pick a random number

# 2. The computer picks a secret number between 1 and 20 and stores it in a box
secret_number = random.randint(1, 20)

print("I am thinking of a number between 1 and 20.")
print("Can you guess what it is?")

# 3. Create a box to hold the player's guess. We start it at 0 (empty).
guess = 0

# 4. Keep looping (repeating) as long as the guess is NOT equal to the secret number
while guess != secret_number:

    # 5. Ask the player for a guess. 
    # We turn their text into a real number using 'int()' so the computer can compare it.
    guess = int(input("Take a guess: "))

    # 6. Check if the guess is too high
    if guess > secret_number:
        print("Too high! Try a lower number.")

    # 7. Check if the guess is too low
    elif guess < secret_number:
        print("Too low! Try a higher number.")

    # 8. If it's not too high and not too low, they must have gotten it right!
    else:
        print("Wow! You guessed it! You win!")