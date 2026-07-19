import random  # 1. Bring in a tool to let the computer pick a random number

# 2. The computer picks a secret number between 1 and 20 and stores it
secret_number = random.randint(1, 20)

print("I am thinking of a number between 1 and 20.")
print("Can you guess what it is?")

# 3. Create a box to hold the player's guess. We start it at 0.
guess = 0

# 4. Keep looping as long as the guess is NOT equal to the secret number
while guess != secret_number:
    
    # --- NEW: INPUT SAFETY LOOP ---
    # This loop runs forever until the user types a valid whole number
    while True:
        try:
            # Try to grab the input and turn it into a number
            guess = int(input("Take a guess: "))
            break  # If successful, 'break' stops this inner loop so the game can continue!
        except ValueError:
            # If the computer hits an error (like someone typing letters), it jumps down here
            print("Oops! That wasn't a valid whole number. Please enter digits only.")
    # ------------------------------

    # 5. Check if the guess is too high
    if guess > secret_number:
        print("Too high! Try a lower number.")

    # 6. Check if the guess is too low
    elif guess < secret_number:
        print("Too low! Try a higher number.")

    # 7. If it's not too high and not too low, they won!
    else:
        print("Wow! You guessed it! You win!")