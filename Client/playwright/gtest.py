# These need to be installed first, if you haven't already:
# python -m pip install playwright
# python -m playwright install

from playwright.sync_api import sync_playwright

def test_google_search():
    with sync_playwright() as p:
        # 1. Open the browser (headless=False lets them watch it happen live!)
        browser = p.chromium.launch(headless=False, slow_mo=1000) # slow_mo slows it down so human eyes can see it
        page = browser.new_page()

        print("1. Navigating to Google...")
        page.goto("https://www.google.com")

        print("2. Filling the search box...")
        # Locates the text box by its name attribute and types into it
        page.fill("textarea[name='q']", "Erbil")

        print("3. Pressing Enter / Clicking search...")
        page.keyboard.press("Enter")

        print("4. Checking if the page loaded correctly...")
        # Verify the page title contains 'Erbil'
        assert "Erbil" in page.title()

        print("SUCCESS: The test passed!")
        # Add a delay of 10 seconds to observe the result before closing the browser
        page.wait_for_timeout(10000)  # Wait for 10 seconds
        browser.close()

if __name__ == "__main__":
    test_google_search()