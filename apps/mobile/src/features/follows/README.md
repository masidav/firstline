# People (follows) in the fan app

`/people` (`PeopleScreen.tsx`): find fans to follow (`GET /users?q=`), the fans you follow (`GET /me/following`),
and your followers (`GET /me/followers`), with Follow / Following, Unfollow, and Remove follower. Following needs no
approval (decided 2026-10-20); followers see your followers-only logs, grab bag, and top fandom stats on their feed.

`FollowButton.tsx` is the toggle used here and on feed cards; `follows.ts` has the API call and `fanName()`.
Reached from the People tab and the profile ("Follows").

Tests: `PeopleScreen.test.tsx`.
